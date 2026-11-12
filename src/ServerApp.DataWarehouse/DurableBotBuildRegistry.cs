using ServerApp.Core;

namespace ServerApp.DataWarehouse;

/// <summary>
/// Coordinates durable bot-build registry commits with a replaceable DataWarehouse hot catalogue.
/// Candidate shelves are built off to the side; they become visible only after the host store
/// confirms an atomic durable commit.
/// </summary>
public sealed class DurableBotBuildRegistry : IRegisteredBotBuildCatalog, IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _mutation = new(1, 1);
    private readonly IBotBuildRegistryStore _store;
    private readonly int _capacity;
    private readonly int _pageSize;
    private DataWarehouseBotBuildCatalog _catalog;
    private long _revision;
    private int _disposed;
    private int _disposeStarted;

    private DurableBotBuildRegistry(
        IBotBuildRegistryStore store,
        int capacity,
        int pageSize,
        BotBuildRegistrySnapshot snapshot,
        DataWarehouseBotBuildCatalog catalog)
    {
        _store = store;
        _capacity = capacity;
        _pageSize = pageSize;
        _revision = snapshot.Revision;
        _catalog = catalog;
    }

    /// <summary>Loads the durable source of truth and reconstructs the process-local hot shelf.</summary>
    public static async ValueTask<DurableBotBuildRegistry> OpenAsync(
        IBotBuildRegistryStore store,
        int capacity,
        int pageSize = 4096,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));

        var snapshot = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var catalog = DataWarehouseBotBuildCatalog.RestoreSnapshot(
            snapshot.Registrations, capacity, pageSize);
        return new DurableBotBuildRegistry(store, capacity, pageSize, snapshot, catalog);
    }

    public long Revision
    {
        get { lock (_gate) { ThrowIfDisposed(); return _revision; } }
    }

    public ValueTask<RegisteredBotBuild?> FindBuildAsync(
        string botIdentityId,
        string buildId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _catalog.FindBuildAsync(botIdentityId, buildId, cancellationToken);
        }
    }

    /// <summary>
    /// Commits a new immutable build before making a replacement hot catalogue visible.
    /// A failed or conflicting durable commit leaves the current catalogue unchanged.
    /// </summary>
    public async ValueTask<BotBuildRegistryMutationResult> RegisterAsync(
        BotBuildReference build,
        BotBuildEligibilityStatus eligibility,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(build);
        await _mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var registrations = _catalog.CreateSnapshot().ToList();
            if (registrations.Any(item =>
                string.Equals(item.Build.BotIdentityId, build.BotIdentityId, StringComparison.Ordinal) &&
                string.Equals(item.Build.BuildId, build.BuildId, StringComparison.Ordinal)))
                return new(false, "The bot identity/build pair is already registered.");

            registrations.Add(new RegisteredBotBuild(build, eligibility));
            return await CommitReplacementAsync(registrations, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutation.Release();
        }
    }

    /// <summary>Persists eligibility changes before publishing the replacement hot catalogue.</summary>
    public async ValueTask<BotBuildRegistryMutationResult> SetEligibilityAsync(
        string botIdentityId,
        string buildId,
        BotBuildEligibilityStatus eligibility,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botIdentityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);
        if (!Enum.IsDefined(eligibility))
            return new(false, "The build eligibility status is not recognized.");

        await _mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var registrations = _catalog.CreateSnapshot().ToList();
            var index = registrations.FindIndex(item =>
                string.Equals(item.Build.BotIdentityId, botIdentityId, StringComparison.Ordinal) &&
                string.Equals(item.Build.BuildId, buildId, StringComparison.Ordinal));
            if (index < 0)
                return new(false, "The exact bot identity/build pair is not registered.");

            var current = registrations[index];
            if (current.Eligibility == eligibility)
                return new(true, null);

            registrations[index] = current with { Eligibility = eligibility };
            return await CommitReplacementAsync(registrations, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutation.Release();
        }
    }

    private async ValueTask<BotBuildRegistryMutationResult> CommitReplacementAsync(
        IReadOnlyList<RegisteredBotBuild> registrations,
        CancellationToken cancellationToken)
    {
        var nextRevision = checked(_revision + 1);
        var replacement = new BotBuildRegistrySnapshot(nextRevision, registrations);

        // Build and validate the complete candidate before committing. It is not yet visible.
        var candidate = DataWarehouseBotBuildCatalog.RestoreSnapshot(
            replacement.Registrations, _capacity, _pageSize);
        BotBuildRegistryCommitResult commit;
        try
        {
            commit = await _store.CommitAsync(_revision, replacement, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            candidate.Dispose();
            throw;
        }

        if (commit != BotBuildRegistryCommitResult.Committed)
        {
            candidate.Dispose();
            return new(false, "The durable registry revision changed; reload before retrying.");
        }

        DataWarehouseBotBuildCatalog previous;
        lock (_gate)
        {
            ThrowIfDisposed();
            previous = _catalog;
            _catalog = candidate;
            _revision = nextRevision;
        }
        previous.Dispose();
        return new(true, null);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
            return;
        _mutation.Wait();
        try
        {
            lock (_gate)
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;
                _catalog.Dispose();
            }
        }
        finally
        {
            _mutation.Release();
            _mutation.Dispose();
        }
    }
}

/// <summary>Result of one durable registry mutation.</summary>
public sealed record BotBuildRegistryMutationResult(bool Committed, string? Error);
