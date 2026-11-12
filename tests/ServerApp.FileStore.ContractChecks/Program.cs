using ServerApp.Core;
using ServerApp.FileStore;

var checks = new (string Name, Func<Task> Run)[]
{
    ("file journal commits and recovers registrations after restart", RecoversRegistration),
    ("file journal applies incremental updates and removals", AppliesDeltas),
    ("file journal arbitrates competing store instances by revision", ArbitratesInstances),
    ("file journal truncates an incomplete final frame and continues", RecoversTornTail),
    ("file journal fails closed on a complete checksum-corrupt frame", RejectsCorruption),
    ("file journal compacts to a checkpoint and reopens", CompactsCheckpoint),
    ("file journal rejects unknown schema metadata", RejectsUnknownSchema)
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
Console.WriteLine($"{checks.Length - failures.Count}/{checks.Length} file-store checks passed.");
return failures.Count == 0 ? 0 : 1;

static async Task RecoversRegistration()
{
    using var directory = new TemporaryDirectory();
    var store = new FileBotBuildRegistryStore(directory.Path);
    var build = Registration("bot-a", "build-a", 'a');
    var committed = await store.CommitAsync(0, Snapshot(1, build));
    Assert(committed == BotBuildRegistryCommitResult.Committed, "The first revision was not committed.");
    var reopened = await new FileBotBuildRegistryStore(directory.Path).LoadAsync();
    Assert(reopened.Revision == 1 && reopened.Registrations.Single() == build,
        "The exact registered build did not survive a store reopen.");
}

static async Task AppliesDeltas()
{
    using var directory = new TemporaryDirectory();
    var store = new FileBotBuildRegistryStore(directory.Path);
    var first = Registration("bot-a", "build-a", 'a');
    var second = Registration("bot-b", "build-b", 'b');
    Assert(await store.CommitAsync(0, Snapshot(1, first, second)) == BotBuildRegistryCommitResult.Committed,
        "Initial registrations failed.");
    var changed = first with { Eligibility = BotBuildEligibilityStatus.Suspended };
    Assert(await store.CommitAsync(1, Snapshot(2, changed)) == BotBuildRegistryCommitResult.Committed,
        "The replacement with an eligibility update and removal failed.");
    var recovered = await store.LoadAsync();
    Assert(recovered.Revision == 2 && recovered.Registrations.Count == 1 &&
           recovered.Registrations[0] == changed,
        "Journal replay did not apply the changed record and explicit removal.");
}

static async Task ArbitratesInstances()
{
    using var directory = new TemporaryDirectory();
    var first = new FileBotBuildRegistryStore(directory.Path);
    var second = new FileBotBuildRegistryStore(directory.Path);
    var outcomes = await Task.WhenAll(
        first.CommitAsync(0, Snapshot(1, Registration("bot-a", "build-a", 'a'))).AsTask(),
        second.CommitAsync(0, Snapshot(1, Registration("bot-b", "build-b", 'b'))).AsTask());
    Assert(outcomes.Count(value => value == BotBuildRegistryCommitResult.Committed) == 1 &&
           outcomes.Count(value => value == BotBuildRegistryCommitResult.RevisionConflict) == 1,
        "Two store instances committed the same expected revision.");
    Assert((await first.LoadAsync()).Registrations.Count == 1,
        "The authoritative store did not retain exactly one winner.");
}

static async Task RecoversTornTail()
{
    using var directory = new TemporaryDirectory();
    var store = new FileBotBuildRegistryStore(directory.Path);
    Assert(await store.CommitAsync(0, Snapshot(1, Registration("bot-a", "build-a", 'a'))) ==
           BotBuildRegistryCommitResult.Committed, "Initial commit failed.");
    var journal = Path.Combine(directory.Path, "bot-build-registry.journal");
    var validLength = new FileInfo(journal).Length;
    await using (var tail = new FileStream(journal, FileMode.Append, FileAccess.Write, FileShare.Read))
        await tail.WriteAsync(new byte[] { 0x53, 0x42, 0x52, 0x4A, 0x00 });
    var recovered = await store.LoadAsync();
    Assert(recovered.Revision == 1 && new FileInfo(journal).Length == validLength,
        "Recovery did not truncate only the incomplete trailing frame.");
    Assert(await store.CommitAsync(1, Snapshot(2,
        Registration("bot-a", "build-a", 'a'),
        Registration("bot-b", "build-b", 'b'))) == BotBuildRegistryCommitResult.Committed,
        "The store could not append after torn-tail recovery.");
}

static async Task RejectsCorruption()
{
    using var directory = new TemporaryDirectory();
    var store = new FileBotBuildRegistryStore(directory.Path);
    Assert(await store.CommitAsync(0, Snapshot(1, Registration("bot-a", "build-a", 'a'))) ==
           BotBuildRegistryCommitResult.Committed, "Initial commit failed.");
    var journal = Path.Combine(directory.Path, "bot-build-registry.journal");
    var bytes = await File.ReadAllBytesAsync(journal);
    bytes[^1] ^= 0x5A;
    await File.WriteAllBytesAsync(journal, bytes);
    await AssertThrows<InvalidDataException>(() => store.LoadAsync().AsTask(),
        "A complete frame with an invalid checksum was accepted.");
}

static async Task CompactsCheckpoint()
{
    using var directory = new TemporaryDirectory();
    var store = new FileBotBuildRegistryStore(directory.Path);
    Assert(await store.CommitAsync(0, Snapshot(1, Registration("bot-a", "build-a", 'a'))) ==
           BotBuildRegistryCommitResult.Committed, "Initial commit failed.");
    Assert(await store.CommitAsync(1, Snapshot(2,
        Registration("bot-a", "build-a", 'a'),
        Registration("bot-b", "build-b", 'b'))) == BotBuildRegistryCommitResult.Committed,
        "Second commit failed.");
    await store.CompactAsync();
    var journal = Path.Combine(directory.Path, "bot-build-registry.journal");
    Assert(new FileInfo(journal).Length == 0, "Compaction did not rotate the journal after checkpoint installation.");
    var reopened = await new FileBotBuildRegistryStore(directory.Path).LoadAsync();
    Assert(reopened.Revision == 2 && reopened.Registrations.Count == 2,
        "The compacted checkpoint did not reconstruct the authoritative registry.");
}

static async Task RejectsUnknownSchema()
{
    using var directory = new TemporaryDirectory();
    var store = new FileBotBuildRegistryStore(directory.Path);
    Assert(await store.CommitAsync(0, Snapshot(1, Registration("bot-a", "build-a", 'a'))) ==
           BotBuildRegistryCommitResult.Committed, "Initial commit failed.");
    var journal = Path.Combine(directory.Path, "bot-build-registry.journal");
    var bytes = await File.ReadAllBytesAsync(journal);
    bytes[8] = 2;
    await File.WriteAllBytesAsync(journal, bytes);
    await AssertThrows<NotSupportedException>(() => store.LoadAsync().AsTask(),
        "An unknown journal schema version was accepted.");
}

static RegisteredBotBuild Registration(string identity, string build, char hash) =>
    new(new BotBuildReference(identity, build, new string(hash, 64)), BotBuildEligibilityStatus.Eligible);

static BotBuildRegistrySnapshot Snapshot(long revision, params RegisteredBotBuild[] registrations) =>
    new(revision, registrations, BotBuildRegistrySnapshot.CurrentSchemaVersion);

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task AssertThrows<TException>(Func<Task> action, string message)
    where TException : Exception
{
    try { await action(); }
    catch (TException) { return; }
    throw new InvalidOperationException(message);
}

sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "ServerApp.FileStore." + Guid.NewGuid().ToString("N"));
    public TemporaryDirectory() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }
}