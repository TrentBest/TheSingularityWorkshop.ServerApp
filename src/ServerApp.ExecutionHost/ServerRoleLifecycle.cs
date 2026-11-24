using ServerApp.Core;
using TheSingularityWorkshop.FSM_COS;

namespace ServerApp.ExecutionHost;

/// <summary>
/// Optional host capability for roles that support graceful drain and stop.
/// It is deliberately separate from FSM_COS composition and RuntimeAssembly lifetime.
/// </summary>
public interface IServerRoleLifecycleAdapter : IServerRoleRuntimeAdapter
{
    ValueTask<RoleLifecycleResult> DrainAsync(
        ServerRoleManifest manifest,
        RuntimeAssembly assembly,
        CancellationToken cancellationToken);

    ValueTask<RoleLifecycleResult> StopAsync(
        ServerRoleManifest manifest,
        RuntimeAssembly assembly,
        CancellationToken cancellationToken);
}

/// <summary>Explicit outcome from a role drain or stop operation.</summary>
public sealed record RoleLifecycleResult(bool Succeeded, string Detail);

public enum ServerRoleLifecycleState
{
    Running,
    Draining,
    Drained,
    Stopping,
    Stopped
}

/// <summary>
/// Serializes one active role's lifecycle. Drain prevents new work; stop terminates the role.
/// FSM_COS registry cleanup occurs only after the adapter confirms termination. This controller
/// does not unload arbitrary bundle code or claim that registry cleanup reverses bundle side effects.
/// </summary>
public sealed class ServerRoleLifecycleController
{
    private readonly ServerRoleManifest _manifest;
    private readonly RuntimeAssembly _assembly;
    private readonly IServerRoleLifecycleAdapter _adapter;
    private readonly ServerRoleRuntimeComposer _composer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ServerRoleLifecycleState _state = ServerRoleLifecycleState.Running;
    private bool _roleStopConfirmed;

    public ServerRoleLifecycleController(
        ServerRoleManifest manifest,
        RuntimeAssembly assembly,
        IServerRoleLifecycleAdapter adapter,
        ServerRoleRuntimeComposer composer)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        if (!string.Equals(manifest.RoleId, adapter.RoleId, StringComparison.Ordinal))
            throw new ArgumentException("The lifecycle adapter RoleId must match the server-role manifest.", nameof(adapter));
    }

    public ServerRoleLifecycleState State => _state;

    /// <summary>Stops accepting new role work and waits for the adapter's drain contract.</summary>
    public async ValueTask DrainAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state is ServerRoleLifecycleState.Drained or ServerRoleLifecycleState.Stopped)
                return;
            if (_state != ServerRoleLifecycleState.Running)
                throw new InvalidOperationException($"Cannot drain a role while it is {_state}.");

            await DrainCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Drains and stops the role. A failed stop remains retryable; once stop is confirmed,
    /// retries perform only the FSM_COS registry cleanup and never stop the role twice.
    /// </summary>
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state == ServerRoleLifecycleState.Stopped)
                return;

            if (!_roleStopConfirmed)
            {
                if (_state == ServerRoleLifecycleState.Running)
                    await DrainCoreAsync(cancellationToken).ConfigureAwait(false);

                if (_state != ServerRoleLifecycleState.Drained)
                    throw new InvalidOperationException($"Cannot stop a role while it is {_state}.");

                _state = ServerRoleLifecycleState.Stopping;
                try
                {
                    var result = await _adapter.StopAsync(_manifest, _assembly, cancellationToken)
                        .ConfigureAwait(false);
                    if (!result.Succeeded)
                        throw new InvalidOperationException(result.Detail);
                    _roleStopConfirmed = true;
                }
                catch
                {
                    _state = ServerRoleLifecycleState.Drained;
                    throw;
                }
            }

            _state = ServerRoleLifecycleState.Stopping;
            _composer.Forget(_assembly.RuntimeId);
            _state = ServerRoleLifecycleState.Stopped;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask DrainCoreAsync(CancellationToken cancellationToken)
    {
        _state = ServerRoleLifecycleState.Draining;
        try
        {
            var result = await _adapter.DrainAsync(_manifest, _assembly, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded)
                throw new InvalidOperationException(result.Detail);
            _state = ServerRoleLifecycleState.Drained;
        }
        catch
        {
            _state = ServerRoleLifecycleState.Running;
            throw;
        }
    }
}
