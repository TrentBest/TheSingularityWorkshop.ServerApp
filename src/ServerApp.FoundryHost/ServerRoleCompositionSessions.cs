using TheSingularityWorkshop.FSM_COS;

namespace ServerApp.FoundryHost;

/// <summary>
/// Owns one candidate's catalog/composer boundary and retained FSM_COS runtime registry.
/// A factory must return a fresh session for every provisioning candidate.
/// </summary>
public interface IServerRoleCompositionSessionFactory
{
    IServerRoleCompositionSession Create(ServerRoleCompositionDefinition definition);
}

/// <summary>
/// Isolated composition state for one provisioning candidate. Implementations may own
/// candidate-scoped catalogs, verified materializations, and other disposable resources.
/// </summary>
public interface IServerRoleCompositionSession : IAsyncDisposable
{
    RuntimeAssembly Compose(RuntimeManifest manifest);
    bool TrySelect(ulong runtimeId);
    bool Forget(ulong runtimeId);
    bool TryGet(ulong runtimeId, out RuntimeAssembly? assembly);
}

/// <summary>
/// Convenience session factory for an in-process FSM_COS catalog provider. The provider
/// is invoked for each candidate; it must not return a shared mutable catalog.
/// </summary>
public sealed class FsmCosCompositionSessionFactory(
    Func<ServerRoleCompositionDefinition, IMicroBundleCatalog> catalogFactory)
    : IServerRoleCompositionSessionFactory
{
    private readonly Func<ServerRoleCompositionDefinition, IMicroBundleCatalog> _catalogFactory =
        catalogFactory ?? throw new ArgumentNullException(nameof(catalogFactory));

    public IServerRoleCompositionSession Create(ServerRoleCompositionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var catalog = _catalogFactory(definition)
            ?? throw new InvalidOperationException("The candidate catalog factory returned null.");
        return new FsmCosCompositionSession(new FsmCos(catalog));
    }
}

/// <summary>Default isolated session backed by one FSM_COS composer and runtime registry.</summary>
public sealed class FsmCosCompositionSession(IFsmCos composer) : IServerRoleCompositionSession
{
    private readonly RuntimeExperienceHost _host =
        new(composer ?? throw new ArgumentNullException(nameof(composer)));

    public RuntimeAssembly Compose(RuntimeManifest manifest) => _host.ComposeAndRegister(manifest);
    public bool TrySelect(ulong runtimeId) => _host.TryActivate(runtimeId);
    public bool Forget(ulong runtimeId) => _host.Registry.TryRemove(runtimeId);
    public bool TryGet(ulong runtimeId, out RuntimeAssembly? assembly) =>
        _host.Registry.TryGet(runtimeId, out assembly);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
