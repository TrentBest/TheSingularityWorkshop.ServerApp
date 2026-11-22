using ServerApp.Core;
using ServerApp.Economy;
using ServerApp.Simulation;

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
    ("simulation fabric delivers messages in deterministic step and enqueue order", SimulationFabricDeliversInOrder),
    ("simulation fabric deduplicates identical IDs and rejects changed payloads", SimulationFabricDeduplicatesMessages),
    ("simulation fabric expires messages before delivery", SimulationFabricExpiresMessages),
    ("simulation fabric rejects unknown participants and unsupported message types", SimulationFabricRejectsUnknownAndUnsupportedMessages),
    ("simulation fabric isolates participant handler failures", SimulationFabricIsolatesHandlerFailures),
    ("simulation fabric rejects virtual-time regression", SimulationFabricRejectsTimeRegression),
    ("simulation fabric normalizes participant identifiers and message types", SimulationFabricNormalizesIdentifiers),
    ("hosted market economy routes all ledger events through institution participants", HostedMarketEconomyRoutesEvents),
    ("hosted market economy delivery is deterministic across runs", HostedMarketEconomyIsDeterministic),
    ("benign swarm partitions bounded agents and conserves virtual resources", SwarmSimulationPartitionsAndAccounts),
    ("benign swarm replay is deterministic and rejects unbounded dimensions", SwarmSimulationIsDeterministicAndBounded),
    ("swarm participant failure produces an explicit failed outcome", SwarmFaultReportsParticipantFailure),
    ("swarm delayed task expiry produces an explicit incomplete outcome", SwarmFaultReportsExpiredTask),
    ("swarm duplicate task attempt is deduplicated without duplicate work", SwarmFaultDeduplicatesTaskAttempt),
    ("full swarm reports one failed participant without false completion", FullSwarmReportsParticipantFailure),
    ("full swarm reports one expired task as incomplete", FullSwarmReportsExpiredTask),
    ("full swarm deduplicates one task attempt and completes all agents", FullSwarmDeduplicatesTaskAttempt),
    ("swarm ledger rejects mismatched and duplicate acknowledgements", SwarmLedgerValidatesAcknowledgements),
    ("swarm ledger enforces terminal and single-delivery transitions", SwarmLedgerEnforcesTransitions),
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

static async Task SimulationFabricDeliversInOrder()
{
    var participant = new TestSimulationMessageParticipant("household-1", "household",
        ["economy.wage", "economy.purchase"]);
    var fabric = new SimulationMessageFabric("market-run");
    fabric.Register(new TestSimulationMessageParticipant("business-1", "business", ["economy.command"]));
    fabric.Register(participant);

    var firstId = Guid.Parse("50000000-0000-0000-0000-000000000001");
    var secondId = Guid.Parse("50000000-0000-0000-0000-000000000002");
    Assert(fabric.Enqueue(new SimulationMessageEnvelope(firstId, "market-run", "business-1",
        "household-1", "economy.wage", 1, 2, 10, "wage-1")).Disposition ==
        SimulationMessageDisposition.Accepted, "The first message was not accepted.");
    Assert(fabric.Enqueue(new SimulationMessageEnvelope(secondId, "market-run", "business-1",
        "household-1", "economy.purchase", 1, 2, 10, "purchase-1")).Disposition ==
        SimulationMessageDisposition.Accepted, "The second message was not accepted.");

    var early = await fabric.AdvanceToAsync(1);
    Assert(early.Count == 0 && participant.Received.Count == 0,
        "A message was delivered before its virtual delivery step.");

    var delivered = await fabric.AdvanceToAsync(2);
    Assert(delivered.Count == 2 &&
           delivered.All(item => item.Disposition == SimulationMessageDisposition.Delivered) &&
           participant.Received.SequenceEqual([firstId, secondId]),
        "Messages were not delivered in deterministic enqueue order.");
}

static async Task SimulationFabricDeduplicatesMessages()
{
    var sender = new TestSimulationMessageParticipant("sender", "business", ["economy.command"]);
    var recipient = new TestSimulationMessageParticipant("recipient", "household", ["economy.wage"]);
    var fabric = new SimulationMessageFabric("run-1");
    fabric.Register(sender);
    fabric.Register(recipient);
    var id = Guid.Parse("50000000-0000-0000-0000-000000000003");
    var original = new SimulationMessageEnvelope(id, "run-1", "sender", "recipient",
        "economy.wage", 1, 1, 10, "payload");

    Assert(fabric.Enqueue(original).Disposition == SimulationMessageDisposition.Accepted,
        "The original message was not accepted.");
    Assert(fabric.Enqueue(original).Disposition == SimulationMessageDisposition.Duplicate,
        "An identical message retry was not recognized as a duplicate.");
    var changed = new SimulationMessageEnvelope(id, "run-1", "sender", "recipient",
        "economy.wage", 1, 1, 10, "changed");
    Assert(fabric.Enqueue(changed).Disposition == SimulationMessageDisposition.Rejected,
        "A reused message ID with changed content was not rejected.");

    await fabric.AdvanceToAsync(1);
    Assert(recipient.Received.SequenceEqual([id]),
        "A duplicate message was delivered more than once.");
}

static async Task SimulationFabricExpiresMessages()
{
    var sender = new TestSimulationMessageParticipant("sender", "business", ["economy.command"]);
    var recipient = new TestSimulationMessageParticipant("recipient", "household", ["economy.wage"]);
    var fabric = new SimulationMessageFabric("run-1");
    fabric.Register(sender);
    fabric.Register(recipient);
    var id = Guid.NewGuid();
    fabric.Enqueue(new SimulationMessageEnvelope(id, "run-1", "sender", "recipient",
        "economy.wage", 1, 1, 2, "expired"));

    var trace = await fabric.AdvanceToAsync(2);
    Assert(trace.Count == 1 && trace[0].Disposition == SimulationMessageDisposition.Expired &&
           recipient.Received.Count == 0,
        "An expired message reached its recipient.");
}

static async Task SimulationFabricRejectsUnknownAndUnsupportedMessages()
{
    var sender = new TestSimulationMessageParticipant("sender", "business", ["economy.command"]);
    var recipient = new TestSimulationMessageParticipant("recipient", "household", ["economy.wage"]);
    var fabric = new SimulationMessageFabric("run-1");
    fabric.Register(sender);
    fabric.Register(recipient);

    var unknownSender = fabric.Enqueue(new SimulationMessageEnvelope(Guid.NewGuid(), "run-1",
        "unknown", "recipient", "economy.wage", 1, 1, 10, "payload"));
    Assert(unknownSender.Disposition == SimulationMessageDisposition.Rejected,
        "A message from an unknown participant was accepted.");

    var unsupported = fabric.Enqueue(new SimulationMessageEnvelope(Guid.NewGuid(), "run-1",
        "sender", "recipient", "economy.admin", 1, 1, 10, "payload"));
    Assert(unsupported.Disposition == SimulationMessageDisposition.Accepted,
        "A routable message should be queued before recipient vocabulary validation.");
    var delivered = await fabric.AdvanceToAsync(1);
    Assert(delivered.Single().Disposition == SimulationMessageDisposition.Rejected &&
           recipient.Received.Count == 0,
        "The recipient received a message outside its declared vocabulary.");
}

static async Task SimulationFabricIsolatesHandlerFailures()
{
    var failing = new TestSimulationMessageParticipant("failing", "business", ["economy.command"])
    {
        Handler = (_, _) => ValueTask.FromException(new InvalidOperationException("fixture failure"))
    };
    var succeeding = new TestSimulationMessageParticipant("succeeding", "household", ["economy.wage"]);
    var sender = new TestSimulationMessageParticipant("sender", "issuer", ["economy.command"]);
    var fabric = new SimulationMessageFabric("run-1");
    fabric.Register(sender);
    fabric.Register(failing);
    fabric.Register(succeeding);

    fabric.Enqueue(new SimulationMessageEnvelope(Guid.NewGuid(), "run-1", "sender", "failing",
        "economy.command", 1, 1, 10, "fail"));
    fabric.Enqueue(new SimulationMessageEnvelope(Guid.NewGuid(), "run-1", "sender", "succeeding",
        "economy.wage", 1, 1, 10, "succeed"));

    var trace = await fabric.AdvanceToAsync(1);
    Assert(trace.Count == 2 &&
           trace[0].Disposition == SimulationMessageDisposition.Failed &&
           trace[1].Disposition == SimulationMessageDisposition.Delivered &&
           succeeding.Received.Count == 1,
        "A handler failure prevented later independent delivery.");
}

static async Task SimulationFabricRejectsTimeRegression()
{
    var fabric = new SimulationMessageFabric("run-1");
    await fabric.AdvanceToAsync(2);
    try
    {
        await fabric.AdvanceToAsync(1);
        throw new InvalidOperationException("The simulation fabric accepted backwards virtual time.");
    }
    catch (ArgumentOutOfRangeException)
    {
    }
}

static async Task SimulationFabricNormalizesIdentifiers()
{
    var sender = new TestSimulationMessageParticipant(" sender ", " business ", [" economy.command "]);
    var recipient = new TestSimulationMessageParticipant(" household-1 ", " household ", [" economy.wage "]);
    var fabric = new SimulationMessageFabric("normalized-run");
    fabric.Register(sender);
    fabric.Register(recipient);

    var message = new SimulationMessageEnvelope(
        Guid.Parse("50000000-0000-0000-0000-000000000099"),
        "normalized-run", "sender", "household-1", "economy.wage", 1, 1, 10, "normalized");
    var accepted = fabric.Enqueue(message);
    var delivered = await fabric.AdvanceToAsync(1);

    Assert(accepted.Disposition == SimulationMessageDisposition.Accepted &&
           delivered.Count == 1 &&
           delivered[0].Disposition == SimulationMessageDisposition.Delivered &&
           recipient.Received.SequenceEqual([message.MessageId]),
        "Trimmed participant IDs or message types did not resolve consistently with the normalized envelope.");

    var duplicateParticipantRejected = false;
    try
    {
        fabric.Register(new TestSimulationMessageParticipant("sender", "business", ["economy.command"]));
    }
    catch (InvalidOperationException)
    {
        duplicateParticipantRejected = true;
    }
    Assert(duplicateParticipantRejected,
        "A participant ID that differs only by surrounding whitespace was registered twice.");

    var duplicateVocabularyRejected = false;
    try
    {
        fabric.Register(new TestSimulationMessageParticipant("another", "business",
            ["economy.command", " economy.command "]));
    }
    catch (ArgumentException)
    {
        duplicateVocabularyRejected = true;
    }
    Assert(duplicateVocabularyRejected,
        "Message types that normalize to the same identifier were not rejected as duplicates.");
}

static async Task HostedMarketEconomyRoutesEvents()
{
    var result = await HostedMarketEconomySimulation.RunAsync();

    Assert(result.Economy.Events.Count == 7,
        "The hosted simulation did not preserve all seven authoritative Economy events.");
    Assert(result.FabricTrace.Count(item => item.Disposition == SimulationMessageDisposition.Accepted) == 7 &&
           result.FabricTrace.Count(item => item.Disposition == SimulationMessageDisposition.Delivered) == 7,
        "The fabric trace does not contain one acceptance and delivery for every Economy event.");

    var expected = new Dictionary<string, string[]>
    {
        ["treasury"] = ["CurrencyIssued"],
        ["employer"] = ["BusinessCapitalized"],
        ["bank-clearing"] = ["WageAcceptedForClearing"],
        ["bank-reserves"] = ["BankFeePosted"],
        ["household"] = ["HouseholdEndowed", "WageSettled"],
        ["merchant"] = ["PurchaseSettled"]
    };

    Assert(result.Participants.Count == expected.Count,
        "The hosted simulation did not register the expected institution participants.");
    foreach (var (participantId, eventKinds) in expected)
    {
        var participant = result.Participants[participantId];
        Assert(participant.ReceivedEvents.Select(item => item.EventKind).SequenceEqual(eventKinds),
            $"Participant '{participantId}' received an unexpected event sequence.");
    }

    var receivedOperationIds = result.Participants.Values
        .SelectMany(participant => participant.ReceivedEvents)
        .Select(item => item.OperationId)
        .Order()
        .ToArray();
    var authoritativeOperationIds = result.Economy.Events
        .Select(item => item.OperationId)
        .Order()
        .ToArray();
    Assert(receivedOperationIds.SequenceEqual(authoritativeOperationIds),
        "The routed event identities do not match the authoritative Economy receipts.");
    Assert(result.Participants.Values.SelectMany(x => x.ReceivedEvents)
            .All(item => item.OperationId != Guid.Empty &&
                         item.CurrencyId != Guid.Empty &&
                         item.Amount > 0m &&
                         !string.IsNullOrWhiteSpace(item.LedgerOperationKind) &&
                         item.Postings.Count > 0 &&
                         item.Postings.All(posting => posting.CurrencyId == item.CurrencyId) &&
                         item.Postings.Sum(posting => posting.Delta) ==
                             (item.LedgerOperationKind == "Issuance" ? item.Amount : 0m)),
        "A transported Economy event failed its payload contract.");
}

static async Task HostedMarketEconomyIsDeterministic()
{
    var first = await HostedMarketEconomySimulation.RunAsync();
    var second = await HostedMarketEconomySimulation.RunAsync();

    Assert(first.Economy.Balances.OrderBy(pair => pair.Key)
            .SequenceEqual(second.Economy.Balances.OrderBy(pair => pair.Key)),
        "Independent hosted runs produced different Economy balances.");
    Assert(first.FabricTrace.Select(item =>
            (item.MessageId, item.Disposition, item.Step, item.Sequence))
        .SequenceEqual(second.FabricTrace.Select(item =>
            (item.MessageId, item.Disposition, item.Step, item.Sequence))),
        "Independent hosted runs produced different fabric traces.");
    Assert(first.Participants.All(pair =>
            pair.Value.ReceivedEvents.Select(item => item.OperationId)
                .SequenceEqual(second.Participants[pair.Key].ReceivedEvents.Select(item => item.OperationId))),
        "Independent hosted runs produced different participant event histories.");
}

static async Task SwarmSimulationPartitionsAndAccounts()
{
    var result = await AutonomousSwarmSimulation.RunAsync();
    Assert(result.Groups.Count == 3 && result.Agents.Count == 12 &&
           result.AssignedTasks == 12 && result.AcknowledgedTasks == 12,
        "The swarm did not partition and acknowledge all twelve bounded virtual survey tasks.");
    Assert(result.Trace.Count(item => item.Disposition == SimulationMessageDisposition.Accepted) == 27 &&
           result.Trace.Count(item => item.Disposition == SimulationMessageDisposition.Delivered) == 27,
        "The fabric trace does not show acceptance and delivery for all group, task, and acknowledgement messages.");
    Assert(result.TotalInitialResourceUnits == 36 && result.TotalRemainingResourceUnits == 24 &&
           result.Agents.Values.All(agent => agent.RemainingResourceUnits == 2 && agent.Acknowledged),
        "The swarm did not conserve its declared per-agent resource accounting.");
    Assert(result.Groups.Values.All(group => group.AssignedTaskCount == 4 && group.AcknowledgedTaskCount == 4),
        "A sub-swarm did not retain the expected bounded assignment and acknowledgement counts.");
}

static async Task SwarmSimulationIsDeterministicAndBounded()
{
    var first = await AutonomousSwarmSimulation.RunAsync(2, 3);
    var second = await AutonomousSwarmSimulation.RunAsync(2, 3);
    Assert(first.Agents.Keys.Order().SequenceEqual(second.Agents.Keys.Order()) &&
           first.Trace.Select(item => (item.MessageId, item.Disposition, item.Step, item.Sequence))
               .SequenceEqual(second.Trace.Select(item => (item.MessageId, item.Disposition, item.Step, item.Sequence))),
        "Independent swarm runs did not produce identical agent identities and fabric traces.");
    try
    {
        await AutonomousSwarmSimulation.RunAsync(AutonomousSwarmSimulation.MaximumGroups + 1, 1);
        throw new InvalidOperationException("The swarm accepted more than its maximum group count.");
    }
    catch (ArgumentOutOfRangeException)
    {
    }
    try
    {
        await AutonomousSwarmSimulation.RunAsync(1, AutonomousSwarmSimulation.MaximumAgentsPerGroup + 1);
        throw new InvalidOperationException("The swarm accepted more than its maximum agents per group.");
    }
    catch (ArgumentOutOfRangeException)
    {
    }
}

static async Task SwarmFaultReportsParticipantFailure()
{
    var result = await SwarmFaultInjectionSimulation.RunAsync(SwarmFaultMode.ParticipantHandlerFailure);
    Assert(result.Outcome == SwarmFaultOutcome.Failed &&
           result.AcceptedTaskMessages == 1 &&
           result.FailedTaskMessages == 1 &&
           result.DeliveredTaskMessages == 0 &&
           result.CoordinatorAcknowledgements == 0,
        "A participant handler failure was not reported as failed without an acknowledgement.");
}

static async Task SwarmFaultReportsExpiredTask()
{
    var result = await SwarmFaultInjectionSimulation.RunAsync(SwarmFaultMode.DelayedTaskExpiry);
    Assert(result.Outcome == SwarmFaultOutcome.Incomplete &&
           result.AcceptedTaskMessages == 1 &&
           result.ExpiredTaskMessages == 1 &&
           result.DeliveredTaskMessages == 0 &&
           result.CoordinatorAcknowledgements == 0,
        "An expired delayed task was not reported as incomplete without an acknowledgement.");
}

static async Task SwarmFaultDeduplicatesTaskAttempt()
{
    var result = await SwarmFaultInjectionSimulation.RunAsync(SwarmFaultMode.DuplicateTaskAttempt);
    Assert(result.Outcome == SwarmFaultOutcome.Completed &&
           result.AcceptedTaskMessages == 1 &&
           result.DuplicateTaskMessages == 1 &&
           result.DeliveredTaskMessages == 1 &&
           result.CoordinatorAcknowledgements == 1,
        "An identical duplicate task attempt caused duplicate work or an incorrect outcome.");
}

static async Task FullSwarmReportsParticipantFailure()
{
    const string target = "group-02-agent-02";
    var result = await AutonomousSwarmFaultSimulation.RunAsync(
        SwarmFaultMode.ParticipantHandlerFailure, target);
    Assert(result.Outcome == SwarmFaultOutcome.Failed &&
           result.Agents.Count == 12 && result.AssignedTasks == 12 &&
           result.AcknowledgedTasks == 11 &&
           result.Agents[target].State == SwarmAgentExecutionState.Failed &&
           result.Agents.Values.Count(agent => agent.State == SwarmAgentExecutionState.Completed) == 11 &&
           result.TotalInitialResourceUnits == 36 && result.TotalRemainingResourceUnits == 25 &&
           result.Agents[target].RemainingResourceUnits == 3 &&
           result.Agents[target].AssignedTask == "survey-cell:group-02:02",
        "The full swarm did not preserve eleven successful agents and one truthful participant failure.");
}

static async Task FullSwarmReportsExpiredTask()
{
    const string target = "group-02-agent-02";
    var result = await AutonomousSwarmFaultSimulation.RunAsync(
        SwarmFaultMode.DelayedTaskExpiry, target);
    Assert(result.Outcome == SwarmFaultOutcome.Incomplete &&
           result.Agents.Count == 12 && result.AssignedTasks == 12 &&
           result.AcknowledgedTasks == 11 &&
           result.Agents[target].State == SwarmAgentExecutionState.Expired &&
           result.Agents.Values.Count(agent => agent.State == SwarmAgentExecutionState.Completed) == 11 &&
           result.Trace.Any(item => item.Disposition == SimulationMessageDisposition.Expired) &&
           result.TotalInitialResourceUnits == 36 && result.TotalRemainingResourceUnits == 25 &&
           result.Agents[target].RemainingResourceUnits == 3 &&
           result.Agents[target].AssignedTask == "survey-cell:group-02:02",
        "The full swarm did not report one expired task and eleven successful acknowledgements.");
}

static async Task FullSwarmDeduplicatesTaskAttempt()
{
    const string target = "group-02-agent-02";
    var first = await AutonomousSwarmFaultSimulation.RunAsync(
        SwarmFaultMode.DuplicateTaskAttempt, target);
    var second = await AutonomousSwarmFaultSimulation.RunAsync(
        SwarmFaultMode.DuplicateTaskAttempt, target);
    Assert(first.Outcome == SwarmFaultOutcome.Completed &&
           first.Agents.Count == 12 && first.AssignedTasks == 12 &&
           first.AcknowledgedTasks == 12 &&
           first.Agents.Values.All(agent =>
               agent.State == SwarmAgentExecutionState.Completed &&
               agent.RemainingResourceUnits == 2 &&
               agent.CoordinatorAcknowledged) &&
           first.Trace.Count(item => item.Disposition == SimulationMessageDisposition.Duplicate) == 1 &&
           first.TotalInitialResourceUnits == 36 && first.TotalRemainingResourceUnits == 24 &&
           first.Trace.Select(item => (item.MessageId, item.Disposition, item.Step, item.Sequence))
               .SequenceEqual(second.Trace.Select(item =>
                   (item.MessageId, item.Disposition, item.Step, item.Sequence))),
        "The full swarm did not suppress the duplicate while completing all twelve agents deterministically.");
}

static Task SwarmLedgerValidatesAcknowledgements()
{
    const string agentId = "group-01-agent-01";
    const string taskId = "survey-cell:group-01:01";
    var ledger = new SwarmExecutionLedger();
    ledger.Issue(agentId, taskId);
    Assert(ledger.GetState(agentId) == SwarmTaskLifecycle.Issued,
        "A host-issued task did not begin in the Issued state.");

    ledger.MarkDelivered(agentId, taskId);
    try
    {
        ledger.TryAcknowledge(agentId, "survey-cell:group-01:99");
        throw new InvalidOperationException("The ledger accepted an acknowledgement for the wrong task.");
    }
    catch (InvalidOperationException exception) when (
        exception.Message.Contains("did not match", StringComparison.Ordinal))
    {
    }

    Assert(ledger.GetState(agentId) == SwarmTaskLifecycle.Delivered,
        "A mismatched acknowledgement changed the agent lifecycle.");
    Assert(ledger.TryAcknowledge(agentId, taskId),
        "The ledger rejected the matching acknowledgement.");
    Assert(!ledger.TryAcknowledge(agentId, taskId),
        "The ledger accepted a duplicate acknowledgement.");
    Assert(ledger.GetState(agentId) == SwarmTaskLifecycle.Completed &&
           ledger.AcknowledgedCount == 1,
        "The ledger did not retain exactly one completed acknowledgement.");
    AssertThrowsForLedger(() => ledger.MarkExpired(agentId),
        "The ledger expired a completed task.");
    AssertThrowsForLedger(() => ledger.MarkFailed(agentId, taskId),
        "The ledger changed a completed task to failed.");
    return Task.CompletedTask;

    static void AssertThrowsForLedger(Action action, string message)
    {
        var threw = false;
        try { action(); }
        catch (InvalidOperationException) { threw = true; }
        Assert(threw, message);
    }
}

static Task SwarmLedgerEnforcesTransitions()
{
    static void AssertThrows(Action action, string message)
    {
        var threw = false;
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Assert(threw, message);
    }

    var failedLedger = new SwarmExecutionLedger();
    failedLedger.Issue("agent-failed", "task-failed");
    AssertThrows(
        () => failedLedger.Issue("agent-failed", "another-task"),
        "The ledger accepted a second task for one agent.");
    failedLedger.MarkDelivered("agent-failed", "task-failed");
    AssertThrows(
        () => failedLedger.MarkDelivered("agent-failed", "task-failed"),
        "The ledger accepted a second delivery transition.");
    failedLedger.MarkFailed("agent-failed", "task-failed");
    Assert(failedLedger.GetState("agent-failed") == SwarmTaskLifecycle.Failed &&
           !failedLedger.TryAcknowledge("agent-failed", "task-failed"),
        "A failed task was acknowledged or lost its terminal state.");
    AssertThrows(
        () => failedLedger.MarkExpired("agent-failed"),
        "The ledger expired a task that had already failed.");

    var issuedFailureLedger = new SwarmExecutionLedger();
    issuedFailureLedger.Issue("agent-issued-failure", "task-issued-failure");
    issuedFailureLedger.MarkFailed("agent-issued-failure", "task-issued-failure");
    Assert(issuedFailureLedger.GetState("agent-issued-failure") == SwarmTaskLifecycle.Failed,
        "A task could not transition directly from Issued to Failed.");

    var expiredLedger = new SwarmExecutionLedger();
    expiredLedger.Issue("agent-expired", "task-expired");
    var deliveredLedger = new SwarmExecutionLedger();
    deliveredLedger.Issue("agent-delivered", "task-delivered");
    deliveredLedger.MarkDelivered("agent-delivered", "task-delivered");
    AssertThrows(
        () => deliveredLedger.MarkExpired("agent-delivered"),
        "The ledger expired a task after delivery.");

    expiredLedger.MarkExpired("agent-expired");
    Assert(expiredLedger.GetState("agent-expired") == SwarmTaskLifecycle.Expired &&
           !expiredLedger.TryAcknowledge("agent-expired", "task-expired"),
        "An expired task was acknowledged or lost its terminal state.");
    AssertThrows(
        () => expiredLedger.MarkDelivered("agent-expired", "task-expired"),
        "The ledger delivered a task after it expired.");
    AssertThrows(
        () => expiredLedger.MarkFailed("agent-expired", "task-expired"),
        "The ledger changed an expired task to failed.");
    AssertThrows(
        () => expiredLedger.TryAcknowledge("unknown-agent", "task-expired"),
        "The ledger accepted an acknowledgement from an unknown agent.");
    Assert(expiredLedger.GetState("unregistered-agent") == SwarmTaskLifecycle.Unconfirmed &&
           expiredLedger.GetTaskId("unregistered-agent") == string.Empty,
        "An unregistered agent was not represented as unconfirmed without an issued task.");

    var senderLedger = new SwarmExecutionLedger();
    senderLedger.Issue("agent-a", "task-a");
    senderLedger.Issue("agent-b", "task-b");
    senderLedger.MarkDelivered("agent-a", "task-a");
    senderLedger.MarkDelivered("agent-b", "task-b");
    AssertThrows(
        () => senderLedger.TryAcknowledge("agent-b", "task-a"),
        "The ledger accepted agent A's task acknowledgement from agent B.");
    Assert(senderLedger.GetState("agent-a") == SwarmTaskLifecycle.Delivered &&
           senderLedger.GetState("agent-b") == SwarmTaskLifecycle.Delivered &&
           senderLedger.AcknowledgedCount == 0,
        "A cross-agent acknowledgement attempt changed either agent's lifecycle.");

    return Task.CompletedTask;
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class TestSimulationMessageParticipant(
    string participantId,
    string roleId,
    IReadOnlyCollection<string> acceptedMessageTypes) : ISimulationMessageParticipant
{
    public string ParticipantId => participantId;
    public string RoleId => roleId;
    public IReadOnlyCollection<string> AcceptedMessageTypes => acceptedMessageTypes;
    public List<Guid> Received { get; } = [];
    public Func<SimulationMessageEnvelope, CancellationToken, ValueTask>? Handler { get; init; }

    public ValueTask ReceiveAsync(
        SimulationMessageEnvelope message,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Received.Add(message.MessageId);
        return Handler?.Invoke(message, cancellationToken) ?? ValueTask.CompletedTask;
    }
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

