using ServerApp.Core;
using ServerApp.DataWarehouse;

var checks = new (string Name, Func<Task> Run)[]
{
    ("DataWarehouse catalogue stores and resolves immutable build records", DataWarehouseCatalogStoresBuilds),
    ("DataWarehouse catalogue restores from durable domain snapshots", DataWarehouseCatalogRestoresSnapshot),
    ("DataWarehouse dirty snapshots acknowledge only unchanged revisions", DataWarehouseCatalogDirtySnapshots),
    ("DataWarehouse catalogue handles thousands of fixed-record builds", DataWarehouseCatalogHandlesLargeBuildSets),
    ("DataWarehouse catalogue serializes concurrent callers", DataWarehouseCatalogSerializesConcurrentCallers),
    ("DataWarehouse catalogue updates eligibility without changing build identity", DataWarehouseCatalogUpdatesEligibility),
    ("DataWarehouse catalogue rejects duplicates, invalid hashes, and capacity overflow", DataWarehouseCatalogRejectsInvalidRegistration),
    ("durable registry commits before publishing and recovers after restart", DurableRegistryCommitsBeforePublishingAndRecovers),
    ("durable registry leaves visible state unchanged after store failure", DurableRegistryStoreFailureLeavesVisibleState),
    ("durable registry persists eligibility before changing admission state", DurableRegistryPersistsEligibility),
    ("durable registry fails closed on a stale store revision", DurableRegistryRejectsStaleRevision),
    ("durable registry snapshots reject unknown schema versions", DurableRegistryRejectsUnknownSchemaVersion),
    ("separate registry instances arbitrate concurrent durable commits", DurableRegistryArbitratesConcurrentInstances)
};

var failures = new List<string>();
foreach (var check in checks)
{
    try
    {
        await check.Run();
        Console.WriteLine($"PASS {check.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{check.Name}: {exception.Message}");
        Console.WriteLine($"FAIL {failures[^1]}");
    }
}

Console.WriteLine($"{checks.Length - failures.Count}/{checks.Length} DataWarehouse integration checks passed.");
return failures.Count == 0 ? 0 : 1;

static async Task DataWarehouseCatalogStoresBuilds()
{
    using var catalog = new DataWarehouseBotBuildCatalog(capacity: 4, pageSize: 64);
    var build = new BotBuildReference("bot-a", "build-a", new string('a', 64));
    Assert(catalog.TryRegister(build, BotBuildEligibilityStatus.Eligible, out var error),
        $"A valid build could not be registered: {error}");
    var registration = await catalog.FindBuildAsync("bot-a", "build-a");
    Assert(registration is not null &&
           registration.Build.BotIdentityId == build.BotIdentityId &&
           registration.Build.BuildId == build.BuildId &&
           registration.Build.ContentSha256 == new string('A', 64) &&
           registration.Eligibility == BotBuildEligibilityStatus.Eligible &&
           catalog.RegisteredBuildCount == 1,
        "The DataWarehouse shelf did not preserve the exact build identity, SHA-256, and eligibility.");
    var snapshot = catalog.CreateSnapshot();
    Assert(snapshot.Count == 1 && snapshot[0].Build.BotIdentityId == "bot-a" &&
           snapshot[0].Build.BuildId == "build-a",
        "The durable-boundary snapshot did not reproduce the registered domain record.");
}

static async Task DataWarehouseCatalogRestoresSnapshot()
{
    var sourceSnapshot = new[]
    {
        new RegisteredBotBuild(
            new BotBuildReference("bot-a", "build-a", new string('e', 64)),
            BotBuildEligibilityStatus.Eligible),
        new RegisteredBotBuild(
            new BotBuildReference("bot-b", "build-b", new string('f', 64)),
            BotBuildEligibilityStatus.Suspended)
    };
    using var restored = DataWarehouseBotBuildCatalog.RestoreSnapshot(sourceSnapshot, capacity: 4, pageSize: 64);
    var first = await restored.FindBuildAsync("bot-a", "build-a");
    var second = await restored.FindBuildAsync("bot-b", "build-b");
    Assert(restored.RegisteredBuildCount == 2 &&
           first?.Eligibility == BotBuildEligibilityStatus.Eligible &&
           second?.Eligibility == BotBuildEligibilityStatus.Suspended &&
           first.Build.ContentSha256 == new string('E', 64) &&
           second.Build.ContentSha256 == new string('F', 64) &&
           restored.DirtyBuildCount == 0,
        "The DataWarehouse hot catalogue did not faithfully restore a durable domain snapshot.");
}

static Task DataWarehouseCatalogDirtySnapshots()
{
    using var catalog = new DataWarehouseBotBuildCatalog(capacity: 4, pageSize: 64);
    Assert(catalog.TryRegister(new BotBuildReference("bot-a", "build-a", new string('a', 64)),
        BotBuildEligibilityStatus.Eligible, out _) &&
        catalog.TryRegister(new BotBuildReference("bot-b", "build-b", new string('b', 64)),
            BotBuildEligibilityStatus.PendingReview, out _),
        "The initial dirty records could not be registered.");

    var initial = catalog.CaptureDirtySnapshot();
    Assert(initial.Registrations.Count == 2 && catalog.DirtyBuildCount == 2,
        "The initial dirty snapshot did not include both new records.");
    catalog.AcknowledgePersisted(initial);
    Assert(catalog.DirtyBuildCount == 0,
        "A successfully persisted unchanged snapshot did not clear dirty bits.");

    Assert(catalog.TrySetEligibility("bot-a", "build-a", BotBuildEligibilityStatus.Suspended) &&
           catalog.DirtyBuildCount == 1,
        "An eligibility mutation did not mark exactly one record dirty.");
    var captured = catalog.CaptureDirtySnapshot();
    Assert(captured.Registrations.Count == 1 &&
           captured.Registrations[0].Eligibility == BotBuildEligibilityStatus.Suspended,
        "Incremental dirty capture did not isolate the changed build.");

    Assert(catalog.TrySetEligibility("bot-a", "build-a", BotBuildEligibilityStatus.Eligible),
        "The in-flight build eligibility could not be updated.");
    catalog.AcknowledgePersisted(captured);
    Assert(catalog.DirtyBuildCount == 1,
        "Acknowledging an older snapshot incorrectly cleared a newer mutation.");

    var latest = catalog.CaptureDirtySnapshot();
    Assert(latest.Registrations.Single().Eligibility == BotBuildEligibilityStatus.Eligible,
        "The next dirty snapshot did not contain the latest eligibility.");
    catalog.AcknowledgePersisted(latest);
    Assert(catalog.DirtyBuildCount == 0,
        "The latest successfully persisted revision remained dirty.");

    using var otherCatalog = new DataWarehouseBotBuildCatalog(capacity: 1, pageSize: 64);
    try
    {
        otherCatalog.AcknowledgePersisted(latest);
        throw new InvalidOperationException("A snapshot from another catalogue was accepted.");
    }
    catch (ArgumentException)
    {
        return Task.CompletedTask;
    }
}

static async Task DataWarehouseCatalogHandlesLargeBuildSets()
{
    const int count = 4096;
    using var catalog = new DataWarehouseBotBuildCatalog(capacity: count);
    for (var i = 0; i < count; i++)
    {
        var build = new BotBuildReference(
            $"bot-{i:D5}",
            $"build-{i:D5}",
            new string((char)('a' + (i % 6)), 64));
        Assert(catalog.TryRegister(build, BotBuildEligibilityStatus.Eligible, out var error),
            $"Build {i} failed to register: {error}");
    }

    var first = await catalog.FindBuildAsync("bot-00000", "build-00000");
    var last = await catalog.FindBuildAsync("bot-04095", "build-04095");
    Assert(catalog.RegisteredBuildCount == count &&
           first?.Build.ContentSha256 == new string('A', 64) &&
           last?.Build.ContentSha256 == new string('D', 64) &&
           catalog.CreateSnapshot().Count == count,
        "The fixed-record catalogue lost or corrupted records under a multi-thousand-build workload.");
}

static async Task DataWarehouseCatalogSerializesConcurrentCallers()
{
    using var catalog = new DataWarehouseBotBuildCatalog(capacity: 128);
    var duplicate = new BotBuildReference("same-bot", "same-build", new string('a', 64));
    var duplicateAttempts = await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(() =>
    {
        return catalog.TryRegister(duplicate, BotBuildEligibilityStatus.Eligible, out var ignored);
    })));
    Assert(duplicateAttempts.Count(x => x) == 1,
        "Concurrent duplicate registrations were not serialized into exactly one accepted record.");

    var registrations = Enumerable.Range(0, 127).Select(i => Task.Run(() =>
        catalog.TryRegister(
            new BotBuildReference($"bot-{i:D3}", $"build-{i:D3}", new string('b', 64)),
            BotBuildEligibilityStatus.Eligible,
            out _)));
    var results = await Task.WhenAll(registrations);
    Assert(results.All(x => x) && catalog.RegisteredBuildCount == 128,
        "Concurrent independent registrations corrupted the DataWarehouse catalogue.");

    var lookups = await Task.WhenAll(Enumerable.Range(0, 127).Select(i =>
        catalog.FindBuildAsync($"bot-{i:D3}", $"build-{i:D3}").AsTask()));
    Assert(lookups.All(x => x?.Eligibility == BotBuildEligibilityStatus.Eligible),
        "Concurrent catalogue reads returned missing or invalid records.");
}

static async Task DataWarehouseCatalogUpdatesEligibility()
{
    using var catalog = new DataWarehouseBotBuildCatalog(capacity: 2, pageSize: 64);
    var build = new BotBuildReference("bot-a", "build-a", new string('b', 64));
    Assert(catalog.TryRegister(build, BotBuildEligibilityStatus.PendingReview, out _),
        "The pending-review build could not be registered.");
    Assert(catalog.TrySetEligibility("bot-a", "build-a", BotBuildEligibilityStatus.Eligible),
        "The eligibility state could not be updated.");
    var registration = await catalog.FindBuildAsync("bot-a", "build-a");
    Assert(registration?.Eligibility == BotBuildEligibilityStatus.Eligible &&
           registration.Build.ContentSha256 == new string('B', 64),
        "Changing eligibility mutated or lost the immutable build content identity.");
}

static Task DataWarehouseCatalogRejectsInvalidRegistration()
{
    using var catalog = new DataWarehouseBotBuildCatalog(capacity: 1, pageSize: 64);
    Assert(!catalog.TryRegister(new BotBuildReference("bot-a", "bad-hash", "not-a-hash"),
        BotBuildEligibilityStatus.Eligible, out _),
        "A malformed content hash was registered.");
    Assert(catalog.TryRegister(new BotBuildReference("bot-a", "build-a", new string('c', 64)),
        BotBuildEligibilityStatus.PendingReview, out _),
        "The first valid build could not be registered.");
    Assert(!catalog.TryRegister(new BotBuildReference("bot-a", "build-a", new string('c', 64)),
        BotBuildEligibilityStatus.Eligible, out _),
        "A duplicate identity/build pair was registered.");
    Assert(!catalog.TryRegister(new BotBuildReference("bot-b", "build-b", new string('d', 64)),
        BotBuildEligibilityStatus.Eligible, out _),
        "The catalogue accepted a build beyond its fixed shelf capacity.");
    Assert(catalog.RegisteredBuildCount == 1,
        "Rejected registrations changed the live-record count.");
    return Task.CompletedTask;
}


static async Task DurableRegistryCommitsBeforePublishingAndRecovers()
{
    var store = new TestBotBuildRegistryStore();
    using (var registry = await DurableBotBuildRegistry.OpenAsync(store, capacity: 8, pageSize: 64))
    {
        var result = await registry.RegisterAsync(
            new BotBuildReference("bot-a", "build-a", new string('a', 64)),
            BotBuildEligibilityStatus.Eligible);
        var visible = await registry.FindBuildAsync("bot-a", "build-a");
        Assert(result.Committed && registry.Revision == 1 &&
               store.Snapshot.Revision == 1 &&
               visible?.Eligibility == BotBuildEligibilityStatus.Eligible,
            "A successful durable registration was not committed and published at revision 1.");
    }

    using var restarted = await DurableBotBuildRegistry.OpenAsync(store, capacity: 8, pageSize: 64);
    var recovered = await restarted.FindBuildAsync("bot-a", "build-a");
    Assert(restarted.Revision == 1 && recovered is not null &&
           recovered.Build.ContentSha256 == new string('A', 64) &&
           recovered.Eligibility == BotBuildEligibilityStatus.Eligible,
        "A fresh registry instance did not recover the exact durable registration.");
}

static async Task DurableRegistryStoreFailureLeavesVisibleState()
{
    var store = new TestBotBuildRegistryStore();
    using var registry = await DurableBotBuildRegistry.OpenAsync(store, capacity: 8, pageSize: 64);
    store.FailNextCommit = true;
    try
    {
        await registry.RegisterAsync(
            new BotBuildReference("bot-a", "build-a", new string('b', 64)),
            BotBuildEligibilityStatus.Eligible);
        throw new InvalidOperationException("The simulated durable commit failure was not propagated.");
    }
    catch (IOException)
    {
        var visible = await registry.FindBuildAsync("bot-a", "build-a");
        Assert(registry.Revision == 0 && store.Snapshot.Revision == 0 && visible is null,
            "A failed durable commit changed the visible catalogue or durable revision.");
    }
}

static async Task DurableRegistryPersistsEligibility()
{
    var store = new TestBotBuildRegistryStore(new BotBuildRegistrySnapshot(0,
    [
        new RegisteredBotBuild(
            new BotBuildReference("bot-a", "build-a", new string('c', 64)),
            BotBuildEligibilityStatus.PendingReview)
    ]));
    using var registry = await DurableBotBuildRegistry.OpenAsync(store, capacity: 8, pageSize: 64);
    var result = await registry.SetEligibilityAsync("bot-a", "build-a", BotBuildEligibilityStatus.Eligible);
    var visible = await registry.FindBuildAsync("bot-a", "build-a");
    Assert(result.Committed && registry.Revision == 1 &&
           store.Snapshot.Registrations.Single().Eligibility == BotBuildEligibilityStatus.Eligible &&
           visible?.Eligibility == BotBuildEligibilityStatus.Eligible,
        "Eligibility was exposed without a matching durable registry commit.");
}

static async Task DurableRegistryArbitratesConcurrentInstances()
{
    var store = new TestBotBuildRegistryStore();
    using var first = await DurableBotBuildRegistry.OpenAsync(store, capacity: 8, pageSize: 64);
    using var second = await DurableBotBuildRegistry.OpenAsync(store, capacity: 8, pageSize: 64);

    var results = await Task.WhenAll(
        first.RegisterAsync(
            new BotBuildReference("bot-a", "build-a", new string('a', 64)),
            BotBuildEligibilityStatus.Eligible).AsTask(),
        second.RegisterAsync(
            new BotBuildReference("bot-b", "build-b", new string('b', 64)),
            BotBuildEligibilityStatus.Eligible).AsTask());

    Assert(results.Count(result => result.Committed) == 1 &&
           store.Snapshot.Revision == 1 &&
           store.Snapshot.Registrations.Count == 1,
        "Two registry instances committed conflicting replacements against the same store revision.");

    var winner = results[0].Committed ? first : second;
    var loser = results[0].Committed ? second : first;
    var winnerBuild = results[0].Committed ? "build-a" : "build-b";
    var loserBuild = results[0].Committed ? "build-b" : "build-a";
    Assert((await winner.FindBuildAsync(results[0].Committed ? "bot-a" : "bot-b", winnerBuild)) is not null &&
           (await loser.FindBuildAsync(results[0].Committed ? "bot-b" : "bot-a", loserBuild)) is null,
        "The losing registry instance exposed a candidate that did not win the durable compare-and-swap.");

    await loser.ReloadAsync();
    var retry = await loser.RegisterAsync(
        new BotBuildReference(
            results[0].Committed ? "bot-b" : "bot-a",
            loserBuild,
            new string(results[0].Committed ? 'b' : 'a', 64)),
        BotBuildEligibilityStatus.Eligible);
    Assert(retry.Committed && store.Snapshot.Revision == 2 &&
           store.Snapshot.Registrations.Count == 2,
        "The losing registry instance did not reload and retry against the winning revision.");
}

static Task DurableRegistryRejectsUnknownSchemaVersion()
{
    try
    {
        _ = new BotBuildRegistrySnapshot(0, Array.Empty<RegisteredBotBuild>(), schemaVersion: 2);
        throw new InvalidOperationException("An unknown durable snapshot schema version was accepted.");
    }
    catch (NotSupportedException)
    {
        var current = new BotBuildRegistrySnapshot(0, Array.Empty<RegisteredBotBuild>());
        Assert(current.SchemaVersion == BotBuildRegistrySnapshot.CurrentSchemaVersion,
            "A current snapshot did not expose its explicit schema version.");
        return Task.CompletedTask;
    }
}

static async Task DurableRegistryRejectsStaleRevision()
{
    var store = new TestBotBuildRegistryStore();
    using var registry = await DurableBotBuildRegistry.OpenAsync(store, capacity: 8, pageSize: 64);
    store.Snapshot = new BotBuildRegistrySnapshot(1, Array.Empty<RegisteredBotBuild>());
    var result = await registry.RegisterAsync(
        new BotBuildReference("bot-a", "build-a", new string('d', 64)),
        BotBuildEligibilityStatus.Eligible);
    var visible = await registry.FindBuildAsync("bot-a", "build-a");
    Assert(!result.Committed && result.Error is not null &&
           registry.Revision == 0 && visible is null &&
           store.Snapshot.Revision == 1 && store.Snapshot.Registrations.Count == 0,
        "A stale registry instance published a mutation after its compare-and-swap was rejected.");

    await registry.ReloadAsync();
    var retried = await registry.RegisterAsync(
        new BotBuildReference("bot-a", "build-a", new string('d', 64)),
        BotBuildEligibilityStatus.Eligible);
    Assert(retried.Committed && registry.Revision == 2 &&
           (await registry.FindBuildAsync("bot-a", "build-a"))?.Eligibility == BotBuildEligibilityStatus.Eligible,
        "The registry did not reload the authoritative revision before retrying.");
}


sealed class TestBotBuildRegistryStore : IBotBuildRegistryStore
{
    private readonly object _gate = new();
    private BotBuildRegistrySnapshot _snapshot;
    public BotBuildRegistrySnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
        set { lock (_gate) _snapshot = value; }
    }
    public bool FailNextCommit { get; set; }

    public TestBotBuildRegistryStore(BotBuildRegistrySnapshot? initial = null) =>
        _snapshot = initial ?? new BotBuildRegistrySnapshot(0, Array.Empty<RegisteredBotBuild>());

    public ValueTask<BotBuildRegistrySnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return ValueTask.FromResult(_snapshot);
    }

    public ValueTask<BotBuildRegistryCommitResult> CommitAsync(
        long expectedRevision,
        BotBuildRegistrySnapshot replacement,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (FailNextCommit)
            {
                FailNextCommit = false;
                throw new IOException("Simulated durable write failure.");
            }
            if (_snapshot.Revision != expectedRevision)
                return ValueTask.FromResult(BotBuildRegistryCommitResult.RevisionConflict);
            if (replacement.Revision != expectedRevision + 1)
                throw new InvalidOperationException("Replacement revision must advance exactly once.");
            _snapshot = replacement;
            return ValueTask.FromResult(BotBuildRegistryCommitResult.Committed);
        }
    }
}
