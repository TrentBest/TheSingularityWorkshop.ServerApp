using ServerApp.Core;
using ServerApp.ExecutionHost;
using TheSingularityWorkshop.FSM_COS;
using TheSingularityWorkshop.Execution;
using TheSingularityWorkshop.MicroBundleDomain;

internal static class Program
{
    private static async Task Main()
    {
        ProviderEventShaIsDistinctFromWorkflowDefinitionSha();
        await SuccessfulRoleActivationIsAExecutionWorkflow();
        await FailedRoleActivationFailsTheExecutionWorkflow();
        await DuplicatePendingInstanceIsRejected();
        await AcceptedWorkflowCanBeCancelled();
        await RunningWorkflowCanBeCancelled();
        await ComposedRoleUsesExecutionAndFsmCos();
        await CompositionFailurePreventsRoleActivation();
        await NotReadyComposedRoleFailsTheWorkflow();
        Console.WriteLine("All ServerApp Execution host contract checks passed.");
    }

    private static void ProviderEventShaIsDistinctFromWorkflowDefinitionSha()
    {
        const string repository = "owner/repo";
        const string workflowSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string eventSha = "dddddddddddddddddddddddddddddddddddddddd";
        const string candidateSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var createdAt = DateTimeOffset.Parse("2026-11-27T10:00:00Z");
        var mapping = new CiAiRequestTaskMapping(
            1, "request-1", "operation-1", "idempotency-1", "task-1", "correlation-1",
            repository, "refs/heads/work/task-1", candidateSha, ".github/workflows/ci.yml",
            "refs/heads/development", workflowSha, "Build", "Resolve candidate", "Verify candidate",
            "ciai-production", "https://artifacts.example.test/",
            "https://ciai.example.test/api/v1/integrations/ciai/artifact-receipts",
            "ciai-ack-profile", createdAt, createdAt.AddHours(2), TrustedEventSha: eventSha)
        {
            JobKey = "build"
        }.Validate();
        var binding = new CiAiDispatchRunBinding(
            123, 1, repository, ".github/workflows/ci.yml", "refs/heads/development",
            workflowSha, "refs/heads/work/task-1", candidateSha, "workflow_dispatch",
            createdAt.AddMinutes(1), EventSha: eventSha);

        var resolved = ServerAppCiAiEvidenceIdentity.ResolveProviderEventSha(mapping, binding);
        Assert(resolved == eventSha && resolved != workflowSha,
            "provider artifact lookup must use the exact event/head SHA, not the trusted workflow-definition SHA");
    }

    private static async Task SuccessfulRoleActivationIsAExecutionWorkflow()
    {
        await using var host = new ExecutionProvisioningHost(new[] { new ReadyRoleAdapter() });
        var submission = host.Submit(CreateManifest("instance-ready"));
        Assert(submission.Status == WorkflowRunStatus.Accepted, "submission should be accepted but not started");

        var result = await host.StartAsync(submission.RunId);
        Assert(result.Status == WorkflowStatus.Succeeded, "ready role should complete its Execution workflow");
        Assert(host.GetSnapshot(submission.RunId).Status == WorkflowRunStatus.Succeeded,
            "Execution should own the terminal workflow status");
        Assert(host.GetProvisioningResult(submission.RunId)?.IsReady == true,
            "ServerApp should report Ready only from the role adapter result");
    }

    private static async Task FailedRoleActivationFailsTheExecutionWorkflow()
    {
        await using var host = new ExecutionProvisioningHost(new[] { new NotReadyRoleAdapter() });
        var submission = host.Submit(CreateManifest("instance-failed"));
        var result = await host.StartAsync(submission.RunId);

        Assert(result.Status == WorkflowStatus.Failed, "a role that does not become Ready must fail its Execution workflow");
        Assert(host.GetProvisioningResult(submission.RunId)?.State == ServerInstanceState.Failed,
            "the structured ServerApp failure result should remain observable");
    }

    private static async Task DuplicatePendingInstanceIsRejected()
    {
        await using var host = new ExecutionProvisioningHost(new[] { new ReadyRoleAdapter() });
        var first = host.Submit(CreateManifest("instance-duplicate"));
        var rejected = false;
        try
        {
            host.Submit(CreateManifest("instance-duplicate"));
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        Assert(rejected, "a duplicate pending instance must not create a second Execution run");
        await host.StartAsync(first.RunId);
    }

    private static async Task AcceptedWorkflowCanBeCancelled()
    {
        await using var host = new ExecutionProvisioningHost(new[] { new ReadyRoleAdapter() });
        var submission = host.Submit(CreateManifest("instance-cancelled"));
        Assert(host.Cancel(submission.RunId), "an accepted workflow should be cancellable before start");
        Assert(host.GetSnapshot(submission.RunId).Status == WorkflowRunStatus.Cancelled,
            "Execution should record the accepted run as cancelled");
    }

    private static async Task RunningWorkflowCanBeCancelled()
    {
        var adapter = new BlockingRoleAdapter();
        await using var host = new ExecutionProvisioningHost(new[] { adapter });
        var submission = host.Submit(CreateManifest("instance-running-cancel"));
        var execution = host.StartAsync(submission.RunId);
        await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert(host.Cancel(submission.RunId), "a running workflow should accept a cooperative cancellation request");
        var result = await execution;
        Assert(result.Status == WorkflowStatus.Cancelled, "Execution should report the running workflow as cancelled");
        Assert(host.GetSnapshot(submission.RunId).Status == WorkflowRunStatus.Cancelled,
            "the retained Execution snapshot should record cancellation");
    }

    private static async Task ComposedRoleUsesExecutionAndFsmCos()
    {
        var loadOrder = new List<ulong>();
        var dependency = new TestBundle(101, "1.0.0", loadOrder);
        var root = new TestBundle(100, "1.0.0", loadOrder, new[] { 101UL });
        var catalog = new TestBundleCatalog(dependency, root);
        var definition = new ServerRoleCompositionDefinition(
            "contract.composed",
            new[] { new MicroBundleManifestEntry(100, "1.0.0") });
        var composer = new ServerRoleRuntimeComposer(
            new FsmCos(catalog),
            new TestRoleCompositionCatalog(definition));
        var adapter = new RecordingRuntimeAdapter("contract.composed", isReady: true);
        var observer = new RecordingExecutionObserver();
        await using var host = new ExecutionComposedProvisioningHost(composer, new[] { adapter }, observer);

        var submission = host.Submit(CreateManifest("instance-composed", "contract.composed"));
        Assert(submission.Status == WorkflowRunStatus.Accepted, "composed role request should be accepted by Execution");
        var result = await host.StartAsync(submission.RunId);

        Assert(result.Status == WorkflowStatus.Succeeded, "Execution should complete composition and activation steps");
        Assert(host.GetProvisioningResult(submission.RunId)?.IsReady == true,
            "ServerApp should report Ready only after the role runtime adapter confirms it");
        Assert(adapter.ReceivedAssembly is not null, "the role adapter should receive the FSM_COS RuntimeAssembly");
        Assert(adapter.ReceivedAssembly!.TryGetBundle(100, out _), "the requested root bundle should be in RuntimeAssembly");
        Assert(loadOrder.SequenceEqual(new[] { 101UL, 100UL }),
            "FSM_COS should load the dependency before the requested root");
        Assert(host.GetRuntimeAssembly(submission.RunId)?.RuntimeId == adapter.ReceivedAssembly.RuntimeId,
            "the host should retain the composed assembly for the successfully activated role");
        var observations = observer.Observations.ToArray();
        Assert(observations.All(item => item.RunId == submission.RunId),
            "every observation must correlate to the authoritative Execution RunId");
        Assert(observations.Select(item => item.Sequence).SequenceEqual(
            observations.Select(item => item.Sequence).OrderBy(value => value)),
            "observation sequence must be monotonic for one run");
        Assert(observations.Any(item => item.Phase == "bundle" && item.Status == "loaded" &&
            item.BundleId == 100 && item.BundleVersion == "1.0.0"),
            "the host must report the exact loaded bundle identity from RuntimeAssembly");
        Assert(observations.Any(item => item.Phase == "role-adapter" && item.Status == "invocation-started"),
            "the host must report the actual role-adapter invocation boundary");
        Assert(!observations.Any(item => item.Phase == "bundle" && item.Status.StartsWith("invoked", StringComparison.Ordinal)),
            "loaded assembly membership must never be represented as per-bundle invocation");
        Assert(observations.Any(item => item.Phase == "workflow" && item.Status == "succeeded"),
            "the terminal Execution workflow result must be observed");
    }

    private static async Task CompositionFailurePreventsRoleActivation()
    {
        var missingCatalog = new TestBundleCatalog();
        var definition = new ServerRoleCompositionDefinition(
            "contract.missing",
            new[] { new MicroBundleManifestEntry(404, "1.0.0") });
        var composer = new ServerRoleRuntimeComposer(
            new FsmCos(missingCatalog),
            new TestRoleCompositionCatalog(definition));
        var adapter = new RecordingRuntimeAdapter("contract.missing", isReady: true);
        await using var host = new ExecutionComposedProvisioningHost(composer, new[] { adapter });

        var submission = host.Submit(CreateManifest("instance-compose-fail", "contract.missing"));
        var result = await host.StartAsync(submission.RunId);

        Assert(result.Status == WorkflowStatus.Failed, "missing MicroBundle composition should fail the Execution workflow");
        Assert(adapter.StartCount == 0, "role activation must not run when FSM_COS composition fails");
        Assert(host.GetProvisioningResult(submission.RunId)?.State == ServerInstanceState.Failed,
            "composition failure should remain observable as a ServerApp provisioning failure");
    }

    private static async Task NotReadyComposedRoleFailsTheWorkflow()
    {
        var bundle = new TestBundle(200, "1.0.0", new List<ulong>());
        var definition = new ServerRoleCompositionDefinition(
            "contract.not-ready",
            new[] { new MicroBundleManifestEntry(200, "1.0.0") });
        var composer = new ServerRoleRuntimeComposer(
            new FsmCos(new TestBundleCatalog(bundle)),
            new TestRoleCompositionCatalog(definition));
        var adapter = new RecordingRuntimeAdapter("contract.not-ready", isReady: false);
        await using var host = new ExecutionComposedProvisioningHost(composer, new[] { adapter });

        var submission = host.Submit(CreateManifest("instance-role-not-ready", "contract.not-ready"));
        var result = await host.StartAsync(submission.RunId);

        Assert(result.Status == WorkflowStatus.Failed, "role adapter refusal must fail the Execution workflow");
        Assert(host.GetProvisioningResult(submission.RunId)?.IsReady == false,
            "a composed but unready role must not be reported Ready");
        Assert(host.GetRuntimeAssembly(submission.RunId) is null,
            "a failed activation must not remain advertised as the active runtime assembly");
    }

    private static ServerRoleManifest CreateManifest(string instanceId) => new()
    {
        SchemaVersion = 1,
        InstanceId = instanceId,
        RoleId = "contract.ready",
        Dependencies = Array.Empty<string>(),
        Resources = new ResourceRequirements { CpuCores = 1, MemoryMiB = 64 }
    };

    private static ServerRoleManifest CreateManifest(string instanceId, string roleId) => new()
    {
        SchemaVersion = 1,
        InstanceId = instanceId,
        RoleId = roleId,
        Dependencies = Array.Empty<string>(),
        Resources = new ResourceRequirements { CpuCores = 1, MemoryMiB = 64 }
    };

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class RecordingExecutionObserver : IServerRoleExecutionObserver
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<ServerRoleExecutionObservation> _observations = new();
        public IReadOnlyCollection<ServerRoleExecutionObservation> Observations => _observations.ToArray();
        public void OnObservation(ServerRoleExecutionObservation observation) => _observations.Enqueue(observation);
    }

    private sealed class ReadyRoleAdapter : IServerRoleAdapter
    {
        public string RoleId => "contract.ready";

        public ValueTask<RoleStartResult> StartAsync(
            ServerRoleManifest manifest, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new RoleStartResult(true, "Contract fixture reports ready."));
        }
    }

    private sealed class BlockingRoleAdapter : IServerRoleAdapter
    {
        public string RoleId => "contract.ready";
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<RoleStartResult> StartAsync(
            ServerRoleManifest manifest, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new RoleStartResult(true, "The blocking fixture should not complete normally.");
        }
    }

    private sealed class NotReadyRoleAdapter : IServerRoleAdapter
    {
        public string RoleId => "contract.ready";

        public ValueTask<RoleStartResult> StartAsync(
            ServerRoleManifest manifest, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RoleStartResult(false, "Contract fixture refused readiness."));
    }

    private sealed class TestRoleCompositionCatalog(params ServerRoleCompositionDefinition[] definitions)
        : IServerRoleCompositionCatalog
    {
        private readonly IReadOnlyDictionary<string, ServerRoleCompositionDefinition> _definitions =
            definitions.ToDictionary(definition => definition.RoleId, StringComparer.Ordinal);

        public bool TryGetDefinition(string roleId, out ServerRoleCompositionDefinition? definition) =>
            _definitions.TryGetValue(roleId, out definition);
    }

    private sealed class TestBundleCatalog(params IMicroBundle[] bundles) : IMicroBundleCatalog
    {
        private readonly IReadOnlyDictionary<ulong, IMicroBundle> _bundles =
            bundles.ToDictionary(bundle => bundle.Id);

        public bool TryResolve(ulong bundleId, string version, out IMicroBundle? bundle)
        {
            if (_bundles.TryGetValue(bundleId, out var found) &&
                string.Equals(found.Descriptor.Version, version, StringComparison.Ordinal))
            {
                bundle = found;
                return true;
            }

            bundle = null;
            return false;
        }

        public bool TryResolve(ulong bundleId, out IMicroBundle? bundle) =>
            _bundles.TryGetValue(bundleId, out bundle);
    }

    private sealed class TestBundle : IMicroBundle
    {
        private readonly IList<ulong> _loadOrder;

        public TestBundle(
            ulong id,
            string version,
            IList<ulong> loadOrder,
            IEnumerable<ulong>? dependencyIds = null)
        {
            _loadOrder = loadOrder;
            var ids = (dependencyIds ?? Array.Empty<ulong>()).ToArray();
            Descriptor = new MicroBundleDescriptor(
                id,
                version,
                dependencies: ids.Select(dependencyId => new MicroBundleDependency(dependencyId)));
            Dependencies = ids
                .Select(dependencyId => new MicroBundleDependencyRequest(dependencyId, ReadOnlyMemory<byte>.Empty))
                .ToArray();
        }

        public MicroBundleDescriptor Descriptor { get; }
        public ulong Id => Descriptor.Id;
        public IReadOnlyList<MicroBundleDependencyRequest> Dependencies { get; }

        public void Load(IMicroBundleLoadContext context) => _loadOrder.Add(Id);
        public bool Arbitrate(IMicroBundleArbitrationContext context, int roundIndex) => false;
    }

    private sealed class RecordingRuntimeAdapter(string roleId, bool isReady) : IServerRoleRuntimeAdapter
    {
        public string RoleId { get; } = roleId;
        public RuntimeAssembly? ReceivedAssembly { get; private set; }
        public int StartCount { get; private set; }

        public ValueTask<RoleStartResult> StartAsync(
            ServerRoleManifest manifest,
            RuntimeAssembly assembly,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCount++;
            ReceivedAssembly = assembly;
            return ValueTask.FromResult(new RoleStartResult(
                isReady,
                isReady ? "Runtime adapter confirmed readiness." : "Runtime adapter refused readiness."));
        }
    }
}
