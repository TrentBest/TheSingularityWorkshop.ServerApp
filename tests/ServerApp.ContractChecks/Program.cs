using ServerApp.Core;

var checks = new (string Name, Func<Task> Run)[]
{
    ("version 1 JSON manifest parses", VersionOneJsonParses),
    ("unknown JSON properties are rejected", UnknownJsonPropertyIsRejected),
    ("valid manifest reaches ready through registered adapter", ValidManifestReachesReady),
    ("dependencies require previously ready instances", DependenciesRequireReadyInstances),
    ("ready instances satisfy declared dependencies", ReadyInstancesSatisfyDependencies),
    ("duplicate instance activation is rejected", DuplicateInstanceActivationIsRejected),
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

static Task VersionOneJsonParses()
{
    const string json = """
    {
      "SchemaVersion": 1,
      "InstanceId": "json-check",
      "RoleId": "test.ready",
      "Dependencies": [],
      "Resources": { "CpuCores": 1, "MemoryMiB": 128 }
    }
    """;
    var manifest = ServerRoleManifest.Parse(json);
    Assert(manifest.InstanceId == "json-check" && manifest.RoleId == "test.ready",
        "JSON values did not map to the manifest.");
    return Task.CompletedTask;
}

static Task UnknownJsonPropertyIsRejected()
{
    const string json = """
    {
      "SchemaVersion": 1,
      "InstanceId": "json-check",
      "RoleId": "test.ready",
      "Dependencies": [],
      "Resources": { "CpuCores": 1, "MemoryMiB": 128 },
      "Execute": "not-allowed"
    }
    """;
    try
    {
        _ = ServerRoleManifest.Parse(json);
        throw new InvalidOperationException("Unknown property was accepted.");
    }
    catch (System.Text.Json.JsonException)
    {
        return Task.CompletedTask;
    }
}

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




static async Task DependenciesRequireReadyInstances()
{
    var manifest = ValidManifest() with { InstanceId = "dependent", Dependencies = ["identity-01"] };
    var coordinator = new ProvisioningCoordinator([new ReadyAdapter("test.ready")]);
    var result = await coordinator.ProvisionAsync(manifest);
    Assert(result.State == ServerInstanceState.Failed &&
           result.Detail.Contains("identity-01") && result.Detail.Contains("not Ready"),
        "A missing ready dependency did not fail closed.");
}

static async Task ReadyInstancesSatisfyDependencies()
{
    var coordinator = new ProvisioningCoordinator([new ReadyAdapter("test.ready")]);
    var dependency = ValidManifest() with { InstanceId = "identity-01" };
    var dependent = ValidManifest() with { InstanceId = "social-01", Dependencies = ["identity-01"] };
    var first = await coordinator.ProvisionAsync(dependency);
    var second = await coordinator.ProvisionAsync(dependent);
    Assert(first.IsReady && second.IsReady,
        $"Expected dependency and dependent to become Ready; got {first.State}, {second.State}.");
}

static async Task DuplicateInstanceActivationIsRejected()
{
    var coordinator = new ProvisioningCoordinator([new ReadyAdapter("test.ready")]);
    var first = await coordinator.ProvisionAsync(ValidManifest());
    var second = await coordinator.ProvisionAsync(ValidManifest());
    Assert(first.IsReady && second.State == ServerInstanceState.Failed &&
           second.Detail.Contains("already registered or starting"),
        "Duplicate instance activation was not rejected.");
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