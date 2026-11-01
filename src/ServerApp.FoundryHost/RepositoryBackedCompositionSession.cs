using System.Collections.ObjectModel;
using TheSingularityWorkshop.FSM_COS;
using TheSingularityWorkshop.MicroBundleDomain;
using TheSingularityWorkshop.MicroBundleRepository.Core;

namespace ServerApp.FoundryHost;

/// <summary>
/// Host-owned allowlist of precompiled MicroBundle implementations. A registration is
/// keyed by the complete immutable artifact address; artifact bytes are data, never code.
/// </summary>
public interface IServerRolePrecompiledMicroBundleRegistry
{
    bool TryMaterialize(
        MicroBundleArtifact artifact,
        out IMicroBundle? bundle);
}

/// <summary>
/// Exact-address registry for host-compiled MicroBundle implementations. Factories must
/// return a fresh instance for each candidate session.
/// </summary>
public sealed class ServerRolePrecompiledMicroBundleRegistry : IServerRolePrecompiledMicroBundleRegistry
{
    private readonly IReadOnlyDictionary<MicroBundleArtifactAddress, Func<MicroBundleArtifact, IMicroBundle>> _factories;

    public ServerRolePrecompiledMicroBundleRegistry(
        IEnumerable<KeyValuePair<MicroBundleArtifactAddress, Func<MicroBundleArtifact, IMicroBundle>>> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var factories = new Dictionary<MicroBundleArtifactAddress, Func<MicroBundleArtifact, IMicroBundle>>();
        foreach (var registration in registrations)
        {
            ArgumentNullException.ThrowIfNull(registration.Value);
            if (!factories.TryAdd(registration.Key, registration.Value))
                throw new ArgumentException($"Duplicate precompiled MicroBundle registration '{registration.Key}'.", nameof(registrations));
        }

        _factories = new ReadOnlyDictionary<MicroBundleArtifactAddress, Func<MicroBundleArtifact, IMicroBundle>>(factories);
    }

    public bool TryMaterialize(MicroBundleArtifact artifact, out IMicroBundle? bundle)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (!_factories.TryGetValue(artifact.Address, out var factory))
        {
            bundle = null;
            return false;
        }

        bundle = factory(artifact)
            ?? throw new InvalidOperationException($"Precompiled materializer for '{artifact.Address}' returned null.");
        return true;
    }
}

/// <summary>
/// Creates a new repository-backed composition session for each provisioning candidate.
/// The repository and registry are shared services; each session stages its own verified
/// dependency closure and publishes it atomically into a private FSM_COS catalog.
/// </summary>
public sealed class RepositoryBackedCompositionSessionFactory(
    IMicroBundleRepository repository,
    IMicroBundleRepositoryObserver observer,
    IServerRolePrecompiledMicroBundleRegistry materializers)
    : IServerRoleCompositionSessionFactory
{
    private readonly IMicroBundleRepository _repository =
        repository ?? throw new ArgumentNullException(nameof(repository));
    private readonly IMicroBundleRepositoryObserver _observer =
        observer ?? throw new ArgumentNullException(nameof(observer));
    private readonly IServerRolePrecompiledMicroBundleRegistry _materializers =
        materializers ?? throw new ArgumentNullException(nameof(materializers));

    public IServerRoleCompositionSession Create(ServerRoleCompositionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new RepositoryBackedCompositionSession(definition, _repository, _observer, _materializers);
    }
}

/// <summary>
/// Candidate-private repository resolver and FSM_COS catalog. Preparation retrieves and
/// verifies the complete pinned dependency closure before making any bundle visible to FSM_COS.
/// </summary>
public sealed class RepositoryBackedCompositionSession : IServerRoleCompositionSession
{
    private const int MaximumDependencyClosureBundles = 10_000;
    private const long MaximumSingleArtifactBytes = 64L * 1024 * 1024;
    private const long MaximumTotalArtifactBytes = 512L * 1024 * 1024;
    private readonly ServerRoleCompositionDefinition _definition;
    private readonly IMicroBundleRepository _repository;
    private readonly IMicroBundleRepositoryObserver _observer;
    private readonly IServerRolePrecompiledMicroBundleRegistry _materializers;
    private readonly CandidateMicroBundleCatalog _catalog = new();
    private readonly RuntimeExperienceHost _host;
    private bool _prepared;
    private bool _disposed;

    public RepositoryBackedCompositionSession(
        ServerRoleCompositionDefinition definition,
        IMicroBundleRepository repository,
        IMicroBundleRepositoryObserver observer,
        IServerRolePrecompiledMicroBundleRegistry materializers)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        _materializers = materializers ?? throw new ArgumentNullException(nameof(materializers));
        _host = new RuntimeExperienceHost(new FsmCos(_catalog));
    }

    public async ValueTask PrepareAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (_prepared)
            throw new InvalidOperationException("A candidate composition session can be prepared only once.");

        var inventory = await _observer.ListAsync(cancellationToken).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(inventory);

        var staged = new Dictionary<ulong, IMicroBundle>();
        long totalArtifactBytes = 0;
        var resolvedVersions = new Dictionary<ulong, string>();
        var pending = new Queue<MicroBundleVersionRequest>();

        foreach (var root in _definition.RootBundles)
        {
            var request = new MicroBundleVersionRequest(root.BundleId, root.Version);
            if (root.IsPinned)
            {
                var address = MicroBundleArtifactResolver.ResolveExact(new[] { request }, inventory)[request];
                if (!string.Equals(address.ContentHash, root.ContentHash, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Root MicroBundle {root.BundleId} version '{root.Version}' does not match its configured content hash.");
            }

            pending.Enqueue(request);
        }

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = pending.Dequeue();
            if (resolvedVersions.TryGetValue(request.BundleId, out var existingVersion))
            {
                if (!string.Equals(existingVersion, request.Version, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Candidate dependency closure requests MicroBundle {request.BundleId} at conflicting versions '{existingVersion}' and '{request.Version}'.");
                continue;
            }

            var address = MicroBundleArtifactResolver.ResolveExact(new[] { request }, inventory)[request];
            var observedLengths = inventory
                .Where(observation => observation.Address == address)
                .Select(observation => observation.ContentLength)
                .Distinct()
                .ToArray();
            if (observedLengths.Length != 1 || observedLengths[0] < 0)
                throw new InvalidOperationException($"Artifact '{address}' has missing or inconsistent inventory length metadata.");
            if (observedLengths[0] > MaximumSingleArtifactBytes ||
                observedLengths[0] > MaximumTotalArtifactBytes - totalArtifactBytes)
                throw new InvalidOperationException($"Candidate artifact closure exceeds the configured artifact size bounds at '{address}'.");

            var artifact = await _repository.GetAsync(address, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Repository did not return immutable artifact '{address}'.");
            if (artifact.Content.Length != observedLengths[0])
                throw new InvalidOperationException($"Retrieved artifact '{address}' does not match its observed content length.");
            totalArtifactBytes += artifact.Content.Length;
            if (artifact.Address != address)
                throw new InvalidOperationException(
                    $"Repository returned '{artifact.Address}' for exact request '{address}'.");

            if (!_materializers.TryMaterialize(artifact, out var bundle) || bundle is null)
                throw new InvalidOperationException(
                    $"No host-approved precompiled MicroBundle implementation is registered for '{address}'.");

            ValidateMaterializedBundle(address, bundle);
            resolvedVersions.Add(request.BundleId, request.Version);
            staged.Add(request.BundleId, bundle);
            if (staged.Count > MaximumDependencyClosureBundles)
                throw new InvalidOperationException(
                    $"Candidate dependency closure exceeds the configured maximum of {MaximumDependencyClosureBundles} MicroBundles.");

            foreach (var dependency in bundle.Dependencies)
            {
                if (!dependency.IsPinned)
                    throw new InvalidOperationException(
                        $"MicroBundle {bundle.Id} declares unpinned dependency {dependency.BundleId}; immutable server composition requires exact versions.");
                pending.Enqueue(new MicroBundleVersionRequest(dependency.BundleId, dependency.Version!));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        _catalog.Publish(staged);
        _prepared = true;
    }

    public RuntimeAssembly Compose(RuntimeManifest manifest)
    {
        ThrowIfDisposed();
        if (!_prepared)
            throw new InvalidOperationException("Candidate artifacts must be fully prepared before FSM_COS composition.");
        return _host.ComposeAndRegister(manifest);
    }

    public bool TrySelect(ulong runtimeId) => !_disposed && _host.TryActivate(runtimeId);

    public bool Forget(ulong runtimeId) => !_disposed && _host.Registry.TryRemove(runtimeId);

    public bool TryGet(ulong runtimeId, out RuntimeAssembly? assembly)
    {
        if (_disposed)
        {
            assembly = null;
            return false;
        }

        return _host.Registry.TryGet(runtimeId, out assembly);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _catalog.Publish(new Dictionary<ulong, IMicroBundle>());
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RepositoryBackedCompositionSession));
    }

    private static void ValidateMaterializedBundle(MicroBundleArtifactAddress address, IMicroBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (bundle.Id != address.BundleId || bundle.Descriptor.Id != address.BundleId ||
            !string.Equals(bundle.Descriptor.Version, address.Version, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Precompiled materializer returned MicroBundle {bundle.Id} version '{bundle.Descriptor.Version}' for artifact '{address}'.");

        var descriptorDependencies = bundle.Descriptor.Dependencies.ToDictionary(dependency => dependency.BundleId);
        var runtimeDependencies = bundle.Dependencies.ToDictionary(dependency => dependency.BundleId);
        if (!descriptorDependencies.Keys.ToHashSet().SetEquals(runtimeDependencies.Keys))
            throw new InvalidOperationException(
                $"MicroBundle {bundle.Id} descriptor dependencies do not match its runtime dependency requests.");

        foreach (var (dependencyId, descriptorDependency) in descriptorDependencies)
        {
            var runtimeDependency = runtimeDependencies[dependencyId];
            if (!descriptorDependency.IsPinned || !runtimeDependency.IsPinned ||
                !string.Equals(descriptorDependency.Version, runtimeDependency.Version, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"MicroBundle {bundle.Id} dependency {dependencyId} must declare one matching exact version in both descriptor and runtime request.");
        }
    }

    private sealed class CandidateMicroBundleCatalog : IMicroBundleCatalog
    {
        private IReadOnlyDictionary<ulong, IMicroBundle> _bundles =
            new ReadOnlyDictionary<ulong, IMicroBundle>(new Dictionary<ulong, IMicroBundle>());

        public void Publish(IDictionary<ulong, IMicroBundle> bundles)
        {
            var snapshot = new ReadOnlyDictionary<ulong, IMicroBundle>(
                new Dictionary<ulong, IMicroBundle>(bundles));
            Volatile.Write(ref _bundles, snapshot);
        }

        public bool TryResolve(ulong bundleId, string version, out IMicroBundle? bundle)
        {
            var snapshot = Volatile.Read(ref _bundles);
            if (snapshot.TryGetValue(bundleId, out var found) &&
                string.Equals(found.Descriptor.Version, version, StringComparison.Ordinal))
            {
                bundle = found;
                return true;
            }

            bundle = null;
            return false;
        }

        public bool TryResolve(ulong bundleId, out IMicroBundle? bundle) =>
            Volatile.Read(ref _bundles).TryGetValue(bundleId, out bundle);
    }
}
