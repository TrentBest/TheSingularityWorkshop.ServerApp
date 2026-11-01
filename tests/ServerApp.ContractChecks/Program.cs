using ServerApp.Core;

var checks = new (string Name, Func<Task> Run)[]
{
    ("valid manifest reaches ready through registered adapter", ValidManifestReachesReady),
    ("unsupported schema is rejected before activation", UnsupportedSchemaIsRejected),
    ("unknown role is not dynamically loaded", UnknownRoleIsRejected),
    ("duplicate dependencies are rejected", DuplicateDependenciesAreRejected),
    ("invalid resource request is rejected", InvalidResourcesAreRejected)
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

Console.WriteLine($"{checks.Length - failures.Count}/{checks.Length} contract checks passed.");
return failures.Count == 0 ? 0 : 1;

static ServerRoleManifest ValidManifest() => new()
{
    SchemaVersion = 1,
    InstanceId = "contract-check",
    RoleId = "test.ready",
    Dependencies = [],
    Resources = new ResourceRequirements { CpuCores = 1, MemoryMiB = 128 }
};

static async Task ValidManifestReachesReady()
{
    var coordinator = new ProvisioningCoordinator([new ReadyAdapter("test.ready")]);
    var result = await coordinator.ProvisionAsync(ValidManifest());
    Assert(result.IsReady, $"Expected Ready; got {result.State}: {result.Detail}");
}

static async Task UnsupportedSchemaIsRejected()
{
    var manifest = ValidManifest() with { SchemaVersion = 99 };
    var coordinator = new ProvisioningCoordinator([new ReadyAdapter("test.ready")]);
    var result = await coordinator.ProvisionAsync(manifest);
    Assert(!result.IsReady && result.ValidationErrors.Any(e => e.Code == "manifest.schema.unsupported"),
        "Unsupported schema was not rejected.");
}

static async Task UnknownRoleIsRejected()
{
    var coordinator = new ProvisioningCoordinator([]);
    var result = await coordinator.ProvisionAsync(ValidManifest());
    Assert(result.State == ServerInstanceState.Failed && result.Detail.Contains("No registered adapter"),
        "Unknown role did not fail closed.");
}

static async Task DuplicateDependenciesAreRejected()
{
    var manifest = ValidManifest() with { Dependencies = ["identity", "identity"] };
    var coordinator = new ProvisioningCoordinator([new ReadyAdapter("test.ready")]);
    var result = await coordinator.ProvisionAsync(manifest);
    Assert(result.ValidationErrors.Any(e => e.Code == "manifest.dependency.duplicate"),
        "Duplicate dependencies were not rejected.");
}

static async Task InvalidResourcesAreRejected()
{
    var manifest = ValidManifest() with
    {
        Resources = new ResourceRequirements { CpuCores = 0, MemoryMiB = 1 }
    };
    var coordinator = new ProvisioningCoordinator([new ReadyAdapter("test.ready")]);
    var result = await coordinator.ProvisionAsync(manifest);
    Assert(result.ValidationErrors.Any(e => e.Code == "manifest.resources.cpu") &&
           result.ValidationErrors.Any(e => e.Code == "manifest.resources.memory"),
        "Invalid resource values were not rejected.");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class ReadyAdapter(string roleId) : IServerRoleAdapter
{
    public string RoleId => roleId;

    public ValueTask<RoleStartResult> StartAsync(
        ServerRoleManifest manifest,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(new RoleStartResult(true, "ready"));
}
