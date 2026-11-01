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
    ("invalid resource request is rejected", InvalidResourcesAreRejected),
    ("batch plan orders dependencies deterministically", BatchPlanOrdersDependenciesDeterministically),
    ("batch plan rejects missing dependencies", BatchPlanRejectsMissingDependencies),
    ("batch plan rejects dependency cycles", BatchPlanRejectsCycles),
    ("batch plan accepts explicitly ready external dependencies", BatchPlanAcceptsReadyExternalDependencies),
    ("batch activation follows dependency order", BatchActivationFollowsDependencyOrder),
    ("batch activation skips failed dependents but continues independent roles", BatchActivationSkipsFailedDependents),
    ("invalid batch plan activates nothing", InvalidBatchPlanActivatesNothing),
    ("host placement is deterministic and accounts for capacity", HostPlacementIsDeterministic),
    ("host placement reports insufficient capacity", HostPlacementReportsInsufficientCapacity),
    ("host placement rejects duplicate host identifiers", HostPlacementRejectsDuplicateHosts),
    ("host placement rejects invalid host snapshots", HostPlacementRejectsInvalidHosts),
    ("host placement continues independent instances when one cannot fit", HostPlacementContinuesIndependentInstances)
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


static Task BatchPlanOrdersDependenciesDeterministically()
{
    var manifests = new[]
    {
        ValidManifest() with { InstanceId = "z-root" },
        ValidManifest() with { InstanceId = "child", Dependencies = ["z-root", "a-root"] },
        ValidManifest() with { InstanceId = "m-root" },
        ValidManifest() with { InstanceId = "a-root" }
    };
    var plan = ProvisioningPlanBuilder.Build(manifests);
    var ids = plan.OrderedManifests.Select(m => m.InstanceId).ToArray();
    Assert(plan.IsValid && ids.SequenceEqual(["a-root", "m-root", "z-root", "child"]),
        $"Unexpected deterministic activation order: {string.Join(", ", ids)}");
    return Task.CompletedTask;
}

static Task BatchPlanRejectsMissingDependencies()
{
    var manifest = ValidManifest() with
    {
        InstanceId = "social-01",
        Dependencies = ["identity-404"]
    };
    var plan = ProvisioningPlanBuilder.Build([manifest]);
    Assert(!plan.IsValid && plan.Errors.Any(e => e.Code == "plan.dependency.missing"),
        "A dependency absent from the batch and ready set was not rejected.");
    return Task.CompletedTask;
}

static Task BatchPlanRejectsCycles()
{
    var first = ValidManifest() with { InstanceId = "first", Dependencies = ["second"] };
    var second = ValidManifest() with { InstanceId = "second", Dependencies = ["first"] };
    var plan = ProvisioningPlanBuilder.Build([first, second]);
    Assert(!plan.IsValid && plan.Errors.Any(e => e.Code == "plan.dependency.cycle"),
        "A dependency cycle was not rejected.");
    return Task.CompletedTask;
}

static Task BatchPlanAcceptsReadyExternalDependencies()
{
    var manifest = ValidManifest() with
    {
        InstanceId = "social-01",
        Dependencies = ["identity-01"]
    };
    var plan = ProvisioningPlanBuilder.Build([manifest], ["identity-01"]);
    Assert(plan.IsValid && plan.OrderedManifests.Single().InstanceId == "social-01",
        "An explicitly Ready dependency outside the batch was not accepted.");
    return Task.CompletedTask;
}


static async Task BatchActivationFollowsDependencyOrder()
{
    var starts = new List<string>();
    var coordinator = new ProvisioningCoordinator([new RecordingAdapter("test.ready", starts)]);
    var batch = new BatchProvisioningCoordinator(coordinator);
    var root = ValidManifest() with { InstanceId = "root" };
    var child = ValidManifest() with { InstanceId = "child", Dependencies = ["root"] };
    var result = await batch.ProvisionAsync([child, root]);
    Assert(result.IsSuccessful && starts.SequenceEqual(["root", "child"]),
        $"Batch did not activate in dependency order: {string.Join(", ", starts)}");
}

static async Task BatchActivationSkipsFailedDependents()
{
    var starts = new List<string>();
    var coordinator = new ProvisioningCoordinator([
        new SelectiveAdapter("test.ready", starts, "root")
    ]);
    var batch = new BatchProvisioningCoordinator(coordinator);
    var manifests = new[]
    {
        ValidManifest() with { InstanceId = "root" },
        ValidManifest() with { InstanceId = "child", Dependencies = ["root"] },
        ValidManifest() with { InstanceId = "independent" }
    };
    var result = await batch.ProvisionAsync(manifests);
    var states = result.Instances.ToDictionary(x => x.InstanceId, x => x.State);
    Assert(!result.IsSuccessful &&
           states["root"] == ServerInstanceState.Failed &&
           states["child"] == ServerInstanceState.Skipped &&
           states["independent"] == ServerInstanceState.Ready &&
           starts.SequenceEqual(["independent", "root"]),
        $"Failure propagation or independent activation was incorrect: {string.Join(", ", starts)}");
}

static async Task InvalidBatchPlanActivatesNothing()
{
    var starts = new List<string>();
    var coordinator = new ProvisioningCoordinator([new RecordingAdapter("test.ready", starts)]);
    var batch = new BatchProvisioningCoordinator(coordinator);
    var invalid = ValidManifest() with { InstanceId = "dependent", Dependencies = ["missing"] };
    var result = await batch.ProvisionAsync([invalid]);
    Assert(!result.PlanAccepted && result.Instances.Count == 0 && starts.Count == 0,
        "An invalid plan started one or more adapters.");
}


static Task HostPlacementIsDeterministic()
{
    var manifests = new[]
    {
        ValidManifest() with { InstanceId = "second", Resources = new ResourceRequirements { CpuCores = 2, MemoryMiB = 256 } },
        ValidManifest() with { InstanceId = "first", Resources = new ResourceRequirements { CpuCores = 1, MemoryMiB = 128 } }
    };
    var hosts = new[]
    {
        new HostCapacityProfile("host-b", 4, 1024),
        new HostCapacityProfile("host-a", 4, 1024)
    };
    var result = HostPlacementPlanner.Build(manifests, hosts);
    Assert(result.IsFullyPlaced &&
           result.Placements.Select(p => p.HostId).SequenceEqual(["host-a", "host-a"]) &&
           result.Placements[0].RemainingCpuCores == 3 &&
           result.Placements[1].RemainingCpuCores == 1 &&
           hosts[1].CpuCores == 4,
        "Placement order, capacity accounting, or input immutability was incorrect.");
    return Task.CompletedTask;
}

static Task HostPlacementReportsInsufficientCapacity()
{
    var manifest = ValidManifest() with
    {
        Resources = new ResourceRequirements { CpuCores = 3, MemoryMiB = 512 }
    };
    var result = HostPlacementPlanner.Build([manifest],
        [new HostCapacityProfile("small-host", 2, 256)]);
    Assert(result.PlanAccepted && !result.IsFullyPlaced &&
           result.Placements.Count == 0 &&
           result.UnplacedInstances.Single().InstanceId == manifest.InstanceId,
        "Insufficient host capacity was not reported as an unplaced instance.");
    return Task.CompletedTask;
}

static Task HostPlacementRejectsDuplicateHosts()
{
    var hosts = new[]
    {
        new HostCapacityProfile("duplicate", 4, 1024),
        new HostCapacityProfile("duplicate", 8, 2048)
    };
    var result = HostPlacementPlanner.Build([ValidManifest()], hosts);
    Assert(!result.PlanAccepted && result.Errors.Any(e => e.Code == "placement.host.duplicate"),
        "Duplicate host identifiers were accepted.");
    return Task.CompletedTask;
}

static Task HostPlacementRejectsInvalidHosts()
{
    var result = HostPlacementPlanner.Build([ValidManifest()],
        [new HostCapacityProfile("", 0, 32)]);
    Assert(!result.PlanAccepted && result.Placements.Count == 0 &&
           result.Errors.Any(e => e.Code == "placement.host.id") &&
           result.Errors.Any(e => e.Code == "placement.host.cpu") &&
           result.Errors.Any(e => e.Code == "placement.host.memory"),
        "Invalid host identity or capacity was accepted.");
    return Task.CompletedTask;
}

static Task HostPlacementContinuesIndependentInstances()
{
    var manifests = new[]
    {
        ValidManifest() with { InstanceId = "too-large", Resources = new ResourceRequirements { CpuCores = 3, MemoryMiB = 512 } },
        ValidManifest() with { InstanceId = "fits" }
    };
    var result = HostPlacementPlanner.Build(manifests,
        [new HostCapacityProfile("host-a", 2, 256)]);
    Assert(result.PlanAccepted && !result.IsFullyPlaced &&
           result.UnplacedInstances.Single().InstanceId == "too-large" &&
           result.Placements.Single().InstanceId == "fits",
        "An unplaceable instance prevented an independent instance from being placed.");
    return Task.CompletedTask;
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

sealed class RecordingAdapter(string roleId, List<string> starts) : IServerRoleAdapter
{
    public string RoleId => roleId;
    public ValueTask<RoleStartResult> StartAsync(ServerRoleManifest manifest, CancellationToken cancellationToken)
    {
        starts.Add(manifest.InstanceId);
        return ValueTask.FromResult(new RoleStartResult(true, "ready"));
    }
}

sealed class SelectiveAdapter(string roleId, List<string> starts, string failedInstanceId) : IServerRoleAdapter
{
    public string RoleId => roleId;
    public ValueTask<RoleStartResult> StartAsync(ServerRoleManifest manifest, CancellationToken cancellationToken)
    {
        starts.Add(manifest.InstanceId);
        return ValueTask.FromResult(new RoleStartResult(
            manifest.InstanceId != failedInstanceId,
            manifest.InstanceId == failedInstanceId ? "simulated failure" : "ready"));
    }
}
