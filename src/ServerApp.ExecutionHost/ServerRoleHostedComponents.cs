using System.Collections.Concurrent;
using ServerApp.Core;
using TheSingularityWorkshop.FSM_COS;

namespace ServerApp.ExecutionHost;

/// <summary>Context supplied only to a host-registered, precompiled role component.</summary>
public sealed record ServerRoleHostedComponentContext(
    ServerRoleManifest Manifest,
    RuntimeAssembly RuntimeAssembly);

/// <summary>
/// A trusted, precompiled component that runs inside the ServerApp process.
/// Implementations must provide explicit readiness, bounded drain, and idempotent stop semantics.
/// </summary>
public interface IServerRoleHostedComponent : IAsyncDisposable
{
    ValueTask StartAsync(ServerRoleHostedComponentContext context, CancellationToken cancellationToken);
    ValueTask WaitForReadyAsync(CancellationToken cancellationToken);
    ValueTask DrainAsync(CancellationToken cancellationToken);
    ValueTask StopAsync(CancellationToken cancellationToken);
}

/// <summary>Host-owned allowlist entry; factories must never be selected from client manifest data.</summary>
public interface IServerRoleHostedComponentFactory
{
    string RoleId { get; }
    IServerRoleHostedComponent Create(ServerRoleHostedComponentContext context);
}

/// <summary>
/// Supervised in-process implementation of the ServerApp role adapter contract.
/// It starts only pre-registered component factories; it never launches shells, scripts,
/// arbitrary executable paths, or assemblies from downloaded artifact bytes.
/// </summary>
public sealed class InProcessServerRoleRuntimeAdapter : IServerRoleLifecycleAdapter
{
    private readonly IReadOnlyDictionary<string, IServerRoleHostedComponentFactory> _factories;
    private readonly ConcurrentDictionary<string, ActiveComponent> _active = new(StringComparer.Ordinal);
    private readonly TimeSpan _readinessTimeout;

    public InProcessServerRoleRuntimeAdapter(
        IEnumerable<IServerRoleHostedComponentFactory> factories,
        TimeSpan readinessTimeout)
    {
        ArgumentNullException.ThrowIfNull(factories);
        if (readinessTimeout <= TimeSpan.Zero || readinessTimeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(readinessTimeout));

        var map = new Dictionary<string, IServerRoleHostedComponentFactory>(StringComparer.Ordinal);
        foreach (var factory in factories)
        {
            ArgumentNullException.ThrowIfNull(factory);
            if (string.IsNullOrWhiteSpace(factory.RoleId))
                throw new ArgumentException("Every hosted component factory must declare a non-empty RoleId.", nameof(factories));
            if (!map.TryAdd(factory.RoleId, factory))
                throw new ArgumentException($"Duplicate hosted component factory '{factory.RoleId}'.", nameof(factories));
        }

        _factories = map;
        _readinessTimeout = readinessTimeout;
    }

    public string RoleId => throw new NotSupportedException(
        "This adapter hosts multiple roles; use its explicit role allowlist.");

    public IReadOnlyCollection<string> GetSupportedRoleIds() => _factories.Keys.ToArray();

    public async ValueTask<RoleStartResult> StartAsync(
        ServerRoleManifest manifest,
        RuntimeAssembly assembly,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(assembly);
        if (!_factories.TryGetValue(manifest.RoleId, out var factory))
            throw new InvalidOperationException($"No precompiled hosted component is registered for role '{manifest.RoleId}'.");

        var context = new ServerRoleHostedComponentContext(manifest, assembly);
        var component = factory.Create(context)
            ?? throw new InvalidOperationException($"Hosted component factory '{manifest.RoleId}' returned null.");
        var active = new ActiveComponent(component);
        if (!_active.TryAdd(manifest.InstanceId, active))
        {
            await component.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"Hosted component instance '{manifest.InstanceId}' is already active.");
        }

        try
        {
            await component.StartAsync(context, cancellationToken).ConfigureAwait(false);
            using var readiness = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readiness.CancelAfter(_readinessTimeout);
            try
            {
                await component.WaitForReadyAsync(readiness.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Hosted component '{manifest.RoleId}' did not report readiness within {_readinessTimeout}.");
            }

            return new RoleStartResult(true,
                $"Precompiled hosted component '{manifest.RoleId}' reported readiness.");
        }
        catch (Exception startException)
        {
            try
            {
                await CleanupFailedStartAsync(manifest.InstanceId, active).ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    $"Hosted component '{manifest.RoleId}' failed to start and cleanup remains pending.",
                    startException, cleanupException);
            }

            throw;
        }
    }

    public async ValueTask<RoleLifecycleResult> DrainAsync(
        ServerRoleManifest manifest,
        RuntimeAssembly assembly,
        CancellationToken cancellationToken)
    {
        if (!_active.TryGetValue(manifest.InstanceId, out var active))
            return new RoleLifecycleResult(true, "No active hosted component remains for this instance.");

        await active.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (active.StopConfirmed || active.Drained)
                return new RoleLifecycleResult(true, "Hosted component is already drained or stopped.");

            try
            {
                await active.Component.DrainAsync(cancellationToken).ConfigureAwait(false);
                active.Drained = true;
                return new RoleLifecycleResult(true, "Hosted component drained.");
            }
            catch (Exception exception)
            {
                return new RoleLifecycleResult(false,
                    $"Hosted component drain failed: {exception.GetType().Name}: {exception.Message}");
            }
        }
        finally
        {
            active.Gate.Release();
        }
    }

    public async ValueTask<RoleLifecycleResult> StopAsync(
        ServerRoleManifest manifest,
        RuntimeAssembly assembly,
        CancellationToken cancellationToken)
    {
        if (!_active.TryGetValue(manifest.InstanceId, out var active))
            return new RoleLifecycleResult(true, "No active hosted component remains for this instance.");

        await active.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!active.StopConfirmed)
            {
                if (!active.Drained)
                {
                    try
                    {
                        await active.Component.DrainAsync(cancellationToken).ConfigureAwait(false);
                        active.Drained = true;
                    }
                    catch (Exception exception)
                    {
                        return new RoleLifecycleResult(false,
                            $"Hosted component drain before stop failed: {exception.GetType().Name}: {exception.Message}");
                    }
                }

                try
                {
                    await active.Component.StopAsync(cancellationToken).ConfigureAwait(false);
                    active.StopConfirmed = true;
                }
                catch (Exception exception)
                {
                    return new RoleLifecycleResult(false,
                        $"Hosted component stop failed: {exception.GetType().Name}: {exception.Message}");
                }
            }

            try
            {
                await active.Component.DisposeAsync().ConfigureAwait(false);
                active.Disposed = true;
                RemoveActive(manifest.InstanceId, active);
                return new RoleLifecycleResult(true, "Hosted component stopped and disposed.");
            }
            catch (Exception exception)
            {
                return new RoleLifecycleResult(false,
                    $"Hosted component stopped but disposal cleanup failed: {exception.GetType().Name}: {exception.Message}");
            }
        }
        finally
        {
            active.Gate.Release();
        }
    }

    private async ValueTask CleanupFailedStartAsync(string instanceId, ActiveComponent active)
    {
        await active.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!active.StopConfirmed)
            {
                await active.Component.StopAsync(CancellationToken.None).ConfigureAwait(false);
                active.StopConfirmed = true;
            }

            if (!active.Disposed)
            {
                await active.Component.DisposeAsync().ConfigureAwait(false);
                active.Disposed = true;
            }

            RemoveActive(instanceId, active);
        }
        finally
        {
            active.Gate.Release();
        }
    }

    private void RemoveActive(string instanceId, ActiveComponent active)
    {
        if (_active.TryGetValue(instanceId, out var current) && ReferenceEquals(current, active))
            _active.TryRemove(instanceId, out _);
    }

    private sealed class ActiveComponent(IServerRoleHostedComponent component)
    {
        public IServerRoleHostedComponent Component { get; } = component;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public bool Drained { get; set; }
        public bool StopConfirmed { get; set; }
        public bool Disposed { get; set; }
    }
}
