namespace ServerApp.Core;

/// <summary>
/// Immutable host-owned durable snapshot of the registered bot-build catalogue.
/// Revision is a store-wide compare-and-swap token, not a DataWarehouse handle or row ID.
/// </summary>
public sealed class BotBuildRegistrySnapshot
{
    public BotBuildRegistrySnapshot(long revision, IEnumerable<RegisteredBotBuild> registrations)
    {
        if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
        ArgumentNullException.ThrowIfNull(registrations);
        Revision = revision;
        Registrations = Array.AsReadOnly(registrations.Select(registration =>
            registration ?? throw new ArgumentException("A registration cannot be null.", nameof(registrations))).ToArray());
    }

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
/// the replacement is durable. Implementations own file/database/replication policy.
/// </summary>
public interface IBotBuildRegistryStore
{
    ValueTask<BotBuildRegistrySnapshot> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask<BotBuildRegistryCommitResult> CommitAsync(
        long expectedRevision,
        BotBuildRegistrySnapshot replacement,
        CancellationToken cancellationToken = default);
}
