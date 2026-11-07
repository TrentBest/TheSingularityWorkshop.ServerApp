using TheSingularityWorkshop.MicroBundleRepository.Local;

namespace ServerApp.FoundryHost;

/// <summary>
/// Development-only composition-session factory backed by an explicitly configured local
/// artifact directory. The same repository instance supplies retrieval and inventory.
/// Production hosts should select a storage adapter and authorization policy deliberately.
/// </summary>
public sealed class LocalFileSystemCompositionSessionFactory : IServerRoleCompositionSessionFactory
{
    private readonly RepositoryBackedCompositionSessionFactory _inner;

    public LocalFileSystemCompositionSessionFactory(
        string artifactRoot,
        IServerRolePrecompiledMicroBundleRegistry materializers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactRoot);
        ArgumentNullException.ThrowIfNull(materializers);

        var repository = new FileSystemMicroBundleRepository(artifactRoot);
        _inner = new RepositoryBackedCompositionSessionFactory(repository, repository, materializers);
        ArtifactRoot = Path.GetFullPath(artifactRoot);
    }

    /// <summary>The normalized, host-configured directory used for immutable bundle artifacts.</summary>
    public string ArtifactRoot { get; }

    public IServerRoleCompositionSession Create(ServerRoleCompositionDefinition definition) =>
        _inner.Create(definition);
}
