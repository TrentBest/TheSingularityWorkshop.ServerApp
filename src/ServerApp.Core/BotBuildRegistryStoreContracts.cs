namespace ServerApp.Core;

/// <summary>
/// Immutable host-owned durable snapshot of the registered bot-build catalogue.
/// Revision is a store-wide compare-and-swap token, not a DataWarehouse handle or row ID.
/// SchemaVersion identifies the durable payload shape. Callers must provide it explicitly; stores
/// must reject missing version metadata and migrate older supported representations before
/// constructing this current domain snapshot.
/// </summary>
public sealed class BotBuildRegistrySnapshot
{
    public const int CurrentSchemaVersion = 1;

    public BotBuildRegistrySnapshot(
        long revision,
        IEnumerable<RegisteredBotBuild> registrations,
        int schemaVersion)
    {
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        if (schemaVersion != CurrentSchemaVersion)
            throw new NotSupportedException($"Bot-build registry schema version {schemaVersion} is not supported; expected {CurrentSchemaVersion}.");
        ArgumentNullException.ThrowIfNull(registrations);
        Revision = revision;
        SchemaVersion = schemaVersion;
        Registrations = Array.AsReadOnly(registrations.Select(registration =>
            registration ?? throw new ArgumentException("A registration cannot be null.", nameof(registrations))).ToArray());
    }

    public int SchemaVersion { get; }
    public long Revision { get; }
    public IReadOnlyList<RegisteredBotBuild> Registrations { get; }
}

/// <summary>Outcome of an atomic durable snapshot compare-and-swap.</summary>
public enum BotBuildRegistryCommitResult
{
    Committed,
    RevisionConflict
}

/// <summary>
/// Host-owned durability boundary. CommitAsync must atomically and durably replace the complete
/// snapshot iff its current revision equals expectedRevision. It must not report Committed before
/// the replacement is durable. The store must persist SchemaVersion with the snapshot, reject
/// payloads with missing version metadata, and migrate older supported payloads to
/// CurrentSchemaVersion before returning them from LoadAsync. Unknown future versions fail closed.
/// An implementation must not throw after a commit has taken effect;
/// it must return Committed for that outcome. If a caller receives a revision conflict, it reloads
/// the authoritative snapshot before retrying. Implementations own file/database/replication policy.
/// </summary>
public interface IBotBuildRegistryStore
{
    ValueTask<BotBuildRegistrySnapshot> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask<BotBuildRegistryCommitResult> CommitAsync(
        long expectedRevision,
        BotBuildRegistrySnapshot replacement,
        CancellationToken cancellationToken = default);
}
