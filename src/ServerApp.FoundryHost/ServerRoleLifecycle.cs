using ServerApp.Core;
using TheSingularityWorkshop.FSM_COS;

namespace ServerApp.FoundryHost;

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
/// Owns the post-activation lifecycle of one successfully started role. Drain prevents new
/// work; stop terminates the role; only confirmed stop permits FSM_COS registry cleanup and
/// composition-session disposal. This controller does not unload arbitrary bundle code.
/// </summary>
public sealed class ServerRoleLifecycleController
{
    private readonly ServerRoleManifest _manifest;
    private readonly RuntimeAssembly _assembly;
    private readonly IServerRoleLifecycleAdapter _adapter;
    private readonly IServerRoleCompositionSession _session;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ServerRoleLifecycleState _state = ServerRoleLifecycleState.Running;

    public ServerRoleLifecycleController(
        ServerRoleManifest manifest,
        RuntimeAssembly assembly,
        IServerRoleLifecycleAdapter adapter,
        IServerRoleCompositionSession session)
    {
        _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        _assembly = assembly ?? throw new ArgumentNullException(nameof(assembly));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _session = session ?? throw new ArgumentNullException(nameof(session));
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
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Gracefully drains a running role, asks the adapter to stop it, then releases the
    /// candidate runtime registry and composition session. A failed stop remains retryable.
    /// </summary>
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state == ServerRoleLifecycleState.Stopped)
                return;
            if (_state == ServerRoleLifecycleState.Running)
            {
                _state = ServerRoleLifecycleState.Draining;
                try
                {
                    var drain = await _adapter.DrainAsync(_manifest, _assembly, cancellationToken)
                        .ConfigureAwait(false);
                    if (!drain.Succeeded)
                        throw new InvalidOperationException(drain.Detail);
                    _state = ServerRoleLifecycleState.Drained;
                }
                catch
                {
                    _state = ServerRoleLifecycleState.Running;
                    throw;
                }
            }

            if (_state != ServerRoleLifecycleState.Drained)
                throw new InvalidOperationException($"Cannot stop a role while it is {_state}.");

            _state = ServerRoleLifecycleState.Stopping;
            RoleLifecycleResult stop;
            try
            {
                stop = await _adapter.StopAsync(_manifest, _assembly, cancellationToken)
                    .ConfigureAwait(false);
                if (!stop.Succeeded)
                    throw new InvalidOperationException(stop.Detail);
            }
            catch
            {
                _state = ServerRoleLifecycleState.Drained;
                throw;
            }

            // The adapter has confirmed process/role stop. Cleanup is now safe to attempt.
            _state = ServerRoleLifecycleState.Stopped;
            _session.Forget(_assembly.RuntimeId);
            await _session.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
