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
    ("host placement continues independent instances when one cannot fit", HostPlacementContinuesIndependentInstances),
    ("host placement skips dependents when a dependency cannot be placed", HostPlacementSkipsUnplacedDependents),
    ("competition exposes the intended team topologies", CompetitionExposesTeamTopologies),
    ("match assignment pins immutable participant builds", MatchAssignmentPinsBuilds),
    ("match assignment rejects unsupported topology", MatchAssignmentRejectsUnsupportedTopology),
    ("match assignment rejects duplicate bot identities and invalid hashes", MatchAssignmentRejectsDuplicateBotsAndInvalidHashes),
    ("match assignment snapshots participant collection", MatchAssignmentSnapshotsParticipants),
    ("1v1 admission resolves two eligible distinct bot builds", MatchAdmissionResolvesEligibleBuilds),
    ("1v1 admission rejects unavailable and mismatched builds", MatchAdmissionRejectsUnavailableAndMismatchedBuilds),
    ("1v1 admission rejects duplicate bot identities before lookup", MatchAdmissionRejectsDuplicateIdentities),
    ("1v1 admission rejects ineligible build states", MatchAdmissionRejectsIneligibleBuilds),
    ("1v1 admission requires the exact requested immutable build", MatchAdmissionRequiresExactBuild),
    ("game session starts only after the adapter reports Ready", GameSessionStartsReady),
    ("game session rejects an adapter version mismatch before launch", GameSessionRejectsAdapterVersionMismatch),
    ("game session rejects mismatched adapter session identity", GameSessionRejectsMismatchedSessionIdentity),
    ("game session accepts only legal lifecycle transitions", GameSessionValidatesLifecycleTransitions),
    ("game session rejects stale lifecycle timestamps", GameSessionRejectsStaleLifecycleTimestamp),
    ("game session stop records Aborted after adapter shutdown", GameSessionStopRecordsAborted),
    ("game session rejects duplicate match starts", GameSessionRejectsDuplicateStarts),
    ("cancelled game-session startup leaves an Aborted record", GameSessionCancellationLeavesAborted),
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


static async Task MatchAdmissionResolvesEligibleBuilds()
{
    var catalog = new TestBotBuildCatalog(new Dictionary<(string, string), RegisteredBotBuild>()
    {
        [("bot-a", "build-a")] = new(new("bot-a", "build-a", new string('a', 64)), BotBuildEligibilityStatus.Eligible),
        [("bot-b", "build-b")] = new(new("bot-b", "build-b", new string('b', 64)), BotBuildEligibilityStatus.Eligible)
    });
    var service = new MatchAdmissionService(TestCompetition(), catalog, "sc2-adapter-1.0.0");
    var result = await service.AdmitAsync(new MatchAdmissionRequest(
        "1v1",
        new MatchAdmissionParticipant("participant-a", "bot-a", "build-a"),
        new MatchAdmissionParticipant("participant-b", "bot-b", "build-b")));

    Assert(result.IsAdmitted && result.Assignment is not null &&
           result.Assignment.Participants.Count == 2 &&
           result.Assignment.Participants.Select(x => x.BotBuild.BotIdentityId)
               .SequenceEqual(["bot-a", "bot-b"]) &&
           CompetitionContractValidator.ValidateAssignment(TestCompetition(), result.Assignment).Count == 0,
        $"A valid 1v1 admission failed: {string.Join("; ", result.Errors.Select(x => x.Message))}");
}

static async Task MatchAdmissionRejectsUnavailableAndMismatchedBuilds()
{
    var catalog = new TestBotBuildCatalog(new Dictionary<(string, string), RegisteredBotBuild>()
    {
        [("bot-a", "build-a")] = new(new("different-bot", "build-a", new string('a', 64)), BotBuildEligibilityStatus.Eligible)
    });
    var service = new MatchAdmissionService(TestCompetition(), catalog, "sc2-adapter-1.0.0");
    var result = await service.AdmitAsync(new MatchAdmissionRequest(
        "1v1",
        new MatchAdmissionParticipant("participant-a", "bot-a", "build-a"),
        new MatchAdmissionParticipant("participant-b", "bot-b", "build-b")));

    Assert(!result.IsAdmitted && result.Assignment is null &&
           result.Errors.Any(x => x.Code == "admission.bot-build.first.identity-mismatch") &&
           result.Errors.Any(x => x.Code == "admission.bot-build.not-found"),
        "Admission accepted an unavailable build or a catalog result for another identity.");
}

static async Task MatchAdmissionRejectsDuplicateIdentities()
{
    var catalog = new TestBotBuildCatalog(new Dictionary<(string, string), RegisteredBotBuild>());
    var service = new MatchAdmissionService(TestCompetition(), catalog, "sc2-adapter-1.0.0");
    var result = await service.AdmitAsync(new MatchAdmissionRequest(
        "1v1",
        new MatchAdmissionParticipant("participant-a", "same-bot", "build-a"),
        new MatchAdmissionParticipant("participant-b", "same-bot", "build-b")));

    Assert(!result.IsAdmitted && result.Assignment is null &&
           result.Errors.Any(x => x.Code == "admission.bot-identity.duplicate") &&
           catalog.LookupCount == 0,
        "Duplicate bot identities were looked up or admitted.");
}

static async Task MatchAdmissionRejectsIneligibleBuilds()
{
    foreach (var status in new[]
    {
        BotBuildEligibilityStatus.PendingReview,
        BotBuildEligibilityStatus.Suspended,
        BotBuildEligibilityStatus.Revoked
    })
    {
        var catalog = new TestBotBuildCatalog(new Dictionary<(string, string), RegisteredBotBuild>
        {
            [("bot-a", "build-a")] = new(new("bot-a", "build-a", new string('a', 64)), status),
            [("bot-b", "build-b")] = new(new("bot-b", "build-b", new string('b', 64)), BotBuildEligibilityStatus.Eligible)
        });
        var service = new MatchAdmissionService(TestCompetition(), catalog, "sc2-adapter-1.0.0");
        var result = await service.AdmitAsync(new MatchAdmissionRequest(
            "1v1",
            new MatchAdmissionParticipant("participant-a", "bot-a", "build-a"),
            new MatchAdmissionParticipant("participant-b", "bot-b", "build-b")));
        Assert(!result.IsAdmitted && result.Errors.Any(x => x.Code == "admission.bot-build.first.ineligible"),
            $"Build status {status} was admitted.");
    }
}

static async Task MatchAdmissionRequiresExactBuild()
{
    var catalog = new TestBotBuildCatalog(new Dictionary<(string, string), RegisteredBotBuild>
    {
        [("bot-a", "build-new")] = new(new("bot-a", "build-new", new string('a', 64)), BotBuildEligibilityStatus.Eligible),
        [("bot-b", "build-b")] = new(new("bot-b", "build-b", new string('b', 64)), BotBuildEligibilityStatus.Eligible)
    });
    var service = new MatchAdmissionService(TestCompetition(), catalog, "sc2-adapter-1.0.0");
    var result = await service.AdmitAsync(new MatchAdmissionRequest(
        "1v1",
        new MatchAdmissionParticipant("participant-a", "bot-a", "build-old"),
        new MatchAdmissionParticipant("participant-b", "bot-b", "build-b")));

    Assert(!result.IsAdmitted && result.Assignment is null &&
           result.Errors.Any(x => x.Code == "admission.bot-build.not-found"),
        "Admission silently substituted a different build for the requested build.");
}

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

static Task CompetitionExposesTeamTopologies()
{
    var competition = new CompetitionDefinition(
        "sc2-ranked",
        "starcraft-ii",
        "ladder-standard",
        "1.0.0",
        [
            new CompetitionMatchFormat("1v1", [1, 1]),
            new CompetitionMatchFormat("2v2", [2, 2]),
            new CompetitionMatchFormat("ffa", [1, 1, 1, 1]),
            new CompetitionMatchFormat("3v3", [3, 3]),
            new CompetitionMatchFormat("4v4", [4, 4]),
            new CompetitionMatchFormat("2v2v2", [2, 2, 2])
        ]);
    var errors = CompetitionContractValidator.ValidateDefinition(competition);
    Assert(errors.Count == 0 && competition.Formats.Count == 6 &&
           competition.Formats.Single(x => x.FormatId == "2v2v2").ParticipantCount == 6,
        $"Expected all six declared team topologies to validate: {string.Join("; ", errors.Select(x => x.Message))}");
    return Task.CompletedTask;
}

static Task MatchAssignmentPinsBuilds()
{
    var competition = TestCompetition();
    var assignment = TestAssignment();
    var errors = CompetitionContractValidator.ValidateAssignment(competition, assignment);
    Assert(errors.Count == 0 && assignment.Participants.Count == 2 &&
           assignment.Participants.All(x => x.BotBuild.ContentSha256.Length == 64),
        $"A valid 1v1 assignment was rejected: {string.Join("; ", errors.Select(x => x.Message))}");
    return Task.CompletedTask;
}

static Task MatchAssignmentRejectsUnsupportedTopology()
{
    var competition = TestCompetition();
    var assignment = WithFormat(TestAssignment(), "2v2");
    var errors = CompetitionContractValidator.ValidateAssignment(competition, assignment);
    Assert(errors.Any(x => x.Code == "match.format.unsupported"),
        "An assignment selected a format not declared by its competition.");
    return Task.CompletedTask;
}

static Task MatchAssignmentRejectsDuplicateBotsAndInvalidHashes()
{
    var competition = TestCompetition();
    var assignment = new MatchAssignment(
        "match-02", "sc2-ranked", "starcraft-ii", "ladder-standard", "1.0.0",
        "1v1", "sc2-adapter-1.0.0", DateTimeOffset.UtcNow,
        [
            new MatchParticipantAssignment("p1", "A", new BotBuildReference("same-bot", "build-a", new string('a', 64))),
            new MatchParticipantAssignment("p2", "B", new BotBuildReference("same-bot", "build-b", "not-a-sha256"))
        ]);
    var errors = CompetitionContractValidator.ValidateAssignment(competition, assignment);
    Assert(errors.Any(x => x.Code == "match.bot-identity.unique") &&
           errors.Any(x => x.Code == "match.bot-build.hash"),
        "Duplicate bot identity or malformed content hash was accepted.");
    return Task.CompletedTask;
}

static Task MatchAssignmentSnapshotsParticipants()
{
    var participants = new List<MatchParticipantAssignment>
    {
        new("p1", "A", new BotBuildReference("bot-a", "build-a", new string('a', 64))),
        new("p2", "B", new BotBuildReference("bot-b", "build-b", new string('b', 64)))
    };
    var assignment = new MatchAssignment(
        "match-03", "sc2-ranked", "starcraft-ii", "ladder-standard", "1.0.0",
        "1v1", "sc2-adapter-1.0.0", DateTimeOffset.UtcNow, participants);
    participants.Clear();
    Assert(assignment.Participants.Count == 2,
        "The assignment retained a mutable caller-owned participant collection.");
    try
    {
        ((IList<MatchParticipantAssignment>)assignment.Participants).Clear();
        throw new InvalidOperationException("The exposed participant snapshot was mutable.");
    }
    catch (NotSupportedException)
    {
        return Task.CompletedTask;
    }
}

static CompetitionDefinition TestCompetition() => new(
    "sc2-ranked", "starcraft-ii", "ladder-standard", "1.0.0",
    [new CompetitionMatchFormat("1v1", [1, 1])]);

static MatchAssignment TestAssignment() => new(
    "match-01", "sc2-ranked", "starcraft-ii", "ladder-standard", "1.0.0",
    "1v1", "sc2-adapter-1.0.0", DateTimeOffset.UtcNow,
    [
        new MatchParticipantAssignment("p1", "A", new BotBuildReference("bot-a", "build-a", new string('a', 64))),
        new MatchParticipantAssignment("p2", "B", new BotBuildReference("bot-b", "build-b", new string('b', 64)))
    ]);

static MatchAssignment WithFormat(MatchAssignment assignment, string formatId) => new(
    assignment.MatchId, assignment.CompetitionId, assignment.GameId, assignment.RulesetId,
    assignment.RulesetVersion, formatId, assignment.GameAdapterVersion, assignment.AssignedAtUtc,
    assignment.Participants);

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

static Task HostPlacementSkipsUnplacedDependents()
{
    var root = ValidManifest() with
    {
        InstanceId = "root",
        Resources = new ResourceRequirements { CpuCores = 3, MemoryMiB = 512 }
    };
    var child = ValidManifest() with
    {
        InstanceId = "child",
        Dependencies = ["root"]
    };
    var independent = ValidManifest() with { InstanceId = "independent" };
    var result = HostPlacementPlanner.Build(
        [child, independent, root],
        [new HostCapacityProfile("small-host", 2, 256)]);

    Assert(result.PlanAccepted &&
           result.Placements.Select(x => x.InstanceId).SequenceEqual(["independent"]) &&
           result.UnplacedInstances.Select(x => x.InstanceId).ToHashSet(StringComparer.Ordinal)
               .SetEquals(["root", "child"]) &&
           result.UnplacedInstances.Single(x => x.InstanceId == "child").Reason.Contains("root"),
        "A dependent was placed even though its required instance could not be placed.");
    return Task.CompletedTask;
}


static async Task GameSessionStartsReady()
{
    var adapter = new TestGameSessionAdapter("sc2-adapter-1.0.0");
    var coordinator = new GameSessionCoordinator(TestCompetition(), adapter);
    var result = await coordinator.StartAsync(TestAssignment());
    Assert(result.IsReady && result.Session?.State == GameSessionState.Ready &&
           result.Session.MatchId == "match-01" && adapter.StartCount == 1,
        "The coordinator did not publish Ready after a successful adapter readiness response.");
}

static async Task GameSessionRejectsAdapterVersionMismatch()
{
    var adapter = new TestGameSessionAdapter("sc2-adapter-1.0.0");
    var coordinator = new GameSessionCoordinator(TestCompetition(), adapter);
    var assignment = new MatchAssignment(
        "match-version", "sc2-ranked", "starcraft-ii", "ladder-standard", "1.0.0",
        "1v1", "sc2-adapter-2.0.0", DateTimeOffset.UtcNow, TestAssignment().Participants);
    var result = await coordinator.StartAsync(assignment);
    Assert(!result.IsReady && result.Errors.Any(error => error.Code == "session.adapter.version") &&
           adapter.StartCount == 0,
        "A match pinned to another adapter version reached the game-session adapter.");
}

static async Task GameSessionRejectsMismatchedSessionIdentity()
{
    var adapter = new TestGameSessionAdapter("sc2-adapter-1.0.0")
    {
        StartFactory = request => new GameSessionSnapshot(
            request.Assignment.MatchId, "adapter-invented-session", "sc2-adapter-1.0.0",
            GameSessionState.Ready, DateTimeOffset.UtcNow)
    };
    var coordinator = new GameSessionCoordinator(TestCompetition(), adapter);
    var result = await coordinator.StartAsync(TestAssignment());
    Assert(!result.IsReady && result.Session?.State == GameSessionState.Failed &&
           result.Errors.Any(error => error.Code == "session.start.invalid-snapshot") &&
           adapter.StopCount == 1,
        "The host accepted an adapter-selected session identity or failed to clean up the invalid session.");
}

static async Task GameSessionValidatesLifecycleTransitions()
{
    var adapter = new TestGameSessionAdapter("sc2-adapter-1.0.0");
    var coordinator = new GameSessionCoordinator(TestCompetition(), adapter);
    var started = await coordinator.StartAsync(TestAssignment());
    Assert(started.IsReady, "The session did not reach Ready.");
    var sessionId = started.Session!.SessionId;

    adapter.StatusSnapshot = new GameSessionSnapshot(
        "match-01", sessionId, adapter.AdapterVersion, GameSessionState.Running, DateTimeOffset.UtcNow);
    var running = await coordinator.RefreshAsync("match-01");
    Assert(running?.State == GameSessionState.Running, "The Ready-to-Running transition was not accepted.");

    adapter.StatusSnapshot = new GameSessionSnapshot(
        "match-01", sessionId, adapter.AdapterVersion, GameSessionState.Completed, DateTimeOffset.UtcNow);
    var completed = await coordinator.RefreshAsync("match-01");
    Assert(completed?.State == GameSessionState.Completed, "The Running-to-Completed transition was not accepted.");

    adapter.StatusSnapshot = new GameSessionSnapshot(
        "match-01", sessionId, adapter.AdapterVersion, GameSessionState.Running, DateTimeOffset.UtcNow);
    var terminal = await coordinator.RefreshAsync("match-01");
    Assert(terminal?.State == GameSessionState.Completed && adapter.StatusCount == 2,
        "A terminal match was refreshed or allowed to transition back to Running.");
}

static async Task GameSessionRejectsStaleLifecycleTimestamp()
{
    var adapter = new TestGameSessionAdapter("sc2-adapter-1.0.0");
    var coordinator = new GameSessionCoordinator(TestCompetition(), adapter);
    var started = await coordinator.StartAsync(TestAssignment());
    adapter.StatusSnapshot = new GameSessionSnapshot(
        "match-01", started.Session!.SessionId, adapter.AdapterVersion,
        GameSessionState.Running, started.Session.ObservedAtUtc.AddSeconds(-1));
    var observed = await coordinator.RefreshAsync("match-01");
    Assert(observed?.State == GameSessionState.Failed &&
           observed.FailureCode == "session.observe.invalid-snapshot",
        "A stale adapter lifecycle timestamp was accepted.");
}

static async Task GameSessionRejectsIllegalTransition()
{
    var adapter = new TestGameSessionAdapter("sc2-adapter-1.0.0");
    var coordinator = new GameSessionCoordinator(TestCompetition(), adapter);
    var started = await coordinator.StartAsync(TestAssignment());
    adapter.StatusSnapshot = new GameSessionSnapshot(
        "match-01", started.Session!.SessionId, adapter.AdapterVersion,
        GameSessionState.Starting, DateTimeOffset.UtcNow);
    var observed = await coordinator.RefreshAsync("match-01");
    Assert(observed?.State == GameSessionState.Failed &&
           observed.FailureCode == "session.transition.invalid",
        "The adapter moved a Ready session backwards without failing closed.");
}

static async Task GameSessionStopRecordsAborted()
{
    var adapter = new TestGameSessionAdapter("sc2-adapter-1.0.0");
    var coordinator = new GameSessionCoordinator(TestCompetition(), adapter);
    var started = await coordinator.StartAsync(TestAssignment());
    var stopped = await coordinator.StopAsync("match-01");
    Assert(started.IsReady && stopped?.State == GameSessionState.Aborted &&
           adapter.StopCount == 1 && stopped.FailureCode is null,
        "A successfully stopped non-terminal session was not recorded as Aborted.");
}

static async Task GameSessionRejectsDuplicateStarts()
{
    var adapter = new TestGameSessionAdapter("sc2-adapter-1.0.0");
    var coordinator = new GameSessionCoordinator(TestCompetition(), adapter);
    var first = await coordinator.StartAsync(TestAssignment());
    var second = await coordinator.StartAsync(TestAssignment());
    Assert(first.IsReady && !second.IsReady &&
           second.Errors.Any(error => error.Code == "session.match.duplicate") &&
           adapter.StartCount == 1,
        "A duplicate match ID launched more than one external game session.");
}

static async Task GameSessionCancellationLeavesAborted()
{
    var adapter = new TestGameSessionAdapter("sc2-adapter-1.0.0");
    var coordinator = new GameSessionCoordinator(TestCompetition(), adapter);
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try
    {
        await coordinator.StartAsync(TestAssignment(), cancellation.Token);
        throw new InvalidOperationException("A pre-cancelled game-session start unexpectedly completed.");
    }
    catch (OperationCanceledException)
    {
        var snapshot = coordinator.GetSnapshot("match-01");
        Assert(snapshot?.State == GameSessionState.Aborted && adapter.StopCount == 1,
            "A cancelled startup did not leave an explicit Aborted state and cleanup attempt.");
    }
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


sealed class TestGameSessionAdapter(string adapterVersion) : IGameSessionAdapter
{
    public string AdapterVersion => adapterVersion;
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public int StatusCount { get; private set; }
    public Func<GameSessionStartRequest, GameSessionSnapshot>? StartFactory { get; init; }
    public GameSessionSnapshot? StatusSnapshot { get; set; }

    public ValueTask<GameSessionSnapshot> StartAsync(
        GameSessionStartRequest request,
        CancellationToken cancellationToken = default)
    {
        StartCount++;
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = StartFactory?.Invoke(request) ?? new GameSessionSnapshot(
            request.Assignment.MatchId, request.SessionId, AdapterVersion,
            GameSessionState.Ready, DateTimeOffset.UtcNow);
        return ValueTask.FromResult(snapshot);
    }

    public ValueTask<GameSessionSnapshot?> GetStatusAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        StatusCount++;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(StatusSnapshot);
    }

    public ValueTask StopAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        StopCount++;
        return ValueTask.CompletedTask;
    }
}

sealed class TestBotBuildCatalog(
    IReadOnlyDictionary<(string BotIdentityId, string BuildId), RegisteredBotBuild> builds)
    : IRegisteredBotBuildCatalog
{
    public int LookupCount { get; private set; }

    public ValueTask<RegisteredBotBuild?> FindBuildAsync(
        string botIdentityId,
        string buildId,
        CancellationToken cancellationToken = default)
    {
        LookupCount++;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            builds.TryGetValue((botIdentityId, buildId), out var registration) ? registration : null);
    }
}


