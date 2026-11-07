using ServerApp.Core;
using ServerApp.FoundryHost;
using System.Security.Cryptography;
using TheSingularityWorkshop.MicroBundleRepository.Core;
using TheSingularityWorkshop.FSM_COS;
using TheSingularityWorkshop.Foundry;
using TheSingularityWorkshop.MicroBundleDomain;
using TheSingularityWorkshop.MicroBundleRepository.Local;

internal static class Program
{
    private static async Task Main()
    {
        await SuccessfulRoleActivationIsAFoundryWorkflow();
        await FailedRoleActivationFailsTheFoundryWorkflow();
        await DuplicatePendingInstanceIsRejected();
        await AcceptedWorkflowCanBeCancelled();
        await RunningWorkflowCanBeCancelled();
        await ComposedRoleUsesFoundryAndFsmCos();
        await ComposedRoleCanDrainStopAndRetryCleanup();
        await ComposedRoleLifecycleCancellationPreservesSession();
        await ConcurrentDrainAndStopSerialize();
        await CompositionFailurePreventsRoleActivation();
        await NotReadyComposedRoleFailsTheWorkflow();
        await ConcurrentCandidatesHaveIsolatedCatalogs();
        await RepositorySessionRejectsConcurrentPreparation();
        await RepositorySessionPreparesVerifiedPinnedDependencyClosure();
        await RepositorySessionDoesNotPublishPartialClosure();
        await LocalFileSystemFactoryPreparesPersistedArtifacts();
        await RepositorySessionRejectsWrongArtifactIdentity();
        await RepositorySessionRejectsInvalidMaterialization();
        await RepositorySessionRejectsUnpinnedTransitiveDependencies();
        await RepositorySessionCancellationDoesNotPublish();
        await RepositorySessionDisposalWinsAgainstLatePreparation();
        Console.WriteLine("All ServerApp Foundry host contract checks passed.");
    }

    private static async Task SuccessfulRoleActivationIsAFoundryWorkflow()
    {
        await using var host = new FoundryProvisioningHost(new[] { new ReadyRoleAdapter() });
        var submission = host.Submit(CreateManifest("instance-ready"));
        Assert(submission.Status == WorkflowRunStatus.Accepted, "submission should be accepted but not started");

        var result = await host.StartAsync(submission.RunId);
        Assert(result.Status == WorkflowStatus.Succeeded, "ready role should complete its Foundry workflow");
        Assert(host.GetSnapshot(submission.RunId).Status == WorkflowRunStatus.Succeeded,
            "Foundry should own the terminal workflow status");
        Assert(host.GetProvisioningResult(submission.RunId)?.IsReady == true,
            "ServerApp should report Ready only from the role adapter result");
    }

    private static async Task FailedRoleActivationFailsTheFoundryWorkflow()
    {
        await using var host = new FoundryProvisioningHost(new[] { new NotReadyRoleAdapter() });
        var submission = host.Submit(CreateManifest("instance-failed"));
        var result = await host.StartAsync(submission.RunId);

        Assert(result.Status == WorkflowStatus.Failed, "a role that does not become Ready must fail its Foundry workflow");
        Assert(host.GetProvisioningResult(submission.RunId)?.State == ServerInstanceState.Failed,
            "the structured ServerApp failure result should remain observable");
    }

    private static async Task DuplicatePendingInstanceIsRejected()
    {
        await using var host = new FoundryProvisioningHost(new[] { new ReadyRoleAdapter() });
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

        Assert(rejected, "a duplicate pending instance must not create a second Foundry run");
        await host.StartAsync(first.RunId);
    }

    private static async Task AcceptedWorkflowCanBeCancelled()
    {
        await using var host = new FoundryProvisioningHost(new[] { new ReadyRoleAdapter() });
        var submission = host.Submit(CreateManifest("instance-cancelled"));
        Assert(host.Cancel(submission.RunId), "an accepted workflow should be cancellable before start");
        Assert(host.GetSnapshot(submission.RunId).Status == WorkflowRunStatus.Cancelled,
            "Foundry should record the accepted run as cancelled");
    }

    private static async Task RunningWorkflowCanBeCancelled()
    {
        var adapter = new BlockingRoleAdapter();
        await using var host = new FoundryProvisioningHost(new[] { adapter });
        var submission = host.Submit(CreateManifest("instance-running-cancel"));
        var execution = host.StartAsync(submission.RunId);
        await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert(host.Cancel(submission.RunId), "a running workflow should accept a cooperative cancellation request");
        var result = await execution;
        Assert(result.Status == WorkflowStatus.Cancelled, "Foundry should report the running workflow as cancelled");
        Assert(host.GetSnapshot(submission.RunId).Status == WorkflowRunStatus.Cancelled,
            "the retained Foundry snapshot should record cancellation");
    }

    private static async Task ComposedRoleUsesFoundryAndFsmCos()
    {
        var loadOrder = new List<ulong>();
        var dependency = new TestBundle(101, "1.0.0", loadOrder);
        var root = new TestBundle(100, "1.0.0", loadOrder, new[] { 101UL });
        var catalog = new TestBundleCatalog(dependency, root);
        var definition = new ServerRoleCompositionDefinition(
            "contract.composed",
            new[] { new MicroBundleManifestEntry(100, "1.0.0") });
        var composer = new ServerRoleRuntimeComposer(
            new FsmCosCompositionSessionFactory(_ => catalog),
            new TestRoleCompositionCatalog(definition));
        var adapter = new RecordingRuntimeAdapter("contract.composed", isReady: true);
        await using var host = new FoundryComposedProvisioningHost(composer, new[] { adapter });

        var submission = host.Submit(CreateManifest("instance-composed", "contract.composed"));
        Assert(submission.Status == WorkflowRunStatus.Accepted, "composed role request should be accepted by Foundry");
        var result = await host.StartAsync(submission.RunId);

        Assert(result.Status == WorkflowStatus.Succeeded, "Foundry should complete composition and activation steps");
        Assert(host.GetProvisioningResult(submission.RunId)?.IsReady == true,
            "ServerApp should report Ready only after the role runtime adapter confirms it");
        Assert(adapter.ReceivedAssembly is not null, "the role adapter should receive the FSM_COS RuntimeAssembly");
        Assert(adapter.ReceivedAssembly!.TryGetBundle(100, out _), "the requested root bundle should be in RuntimeAssembly");
        Assert(loadOrder.SequenceEqual(new[] { 101UL, 100UL }),
            "FSM_COS should load the dependency before the requested root");
        Assert(host.GetRuntimeAssembly(submission.RunId)?.RuntimeId == adapter.ReceivedAssembly.RuntimeId,
            "the host should retain the composed assembly for the successfully activated role");
    }

    private static async Task ComposedRoleCanDrainStopAndRetryCleanup()
    {
        var bundle = new TestBundle(150, "1.0.0", new List<ulong>());
        var definition = new ServerRoleCompositionDefinition(
            "contract.lifecycle",
            new[] { new MicroBundleManifestEntry(150, "1.0.0") });
        var sessions = new TrackingCompositionSessionFactory(_ => new TestBundleCatalog(bundle));
        var composer = new ServerRoleRuntimeComposer(sessions, new TestRoleCompositionCatalog(definition));
        var adapter = new LifecycleRoleAdapter("contract.lifecycle") { FailFirstStop = true };
        await using var host = new FoundryComposedProvisioningHost(composer, new[] { adapter });

        var submission = host.Submit(CreateManifest("instance-lifecycle", "contract.lifecycle"));
        var result = await host.StartAsync(submission.RunId);
        Assert(result.Status == WorkflowStatus.Succeeded, "lifecycle-capable role should activate successfully");
        Assert(host.GetLifecycleController(submission.RunId) is not null,
            "successful activation should expose its host-owned lifecycle handle");

        await host.DrainRoleAsync(submission.RunId);
        Assert(host.GetLifecycleController(submission.RunId)!.State == ServerRoleLifecycleState.Drained,
            "explicit drain should transition the retained role to Drained");

        var firstStopFailed = false;
        try
        {
            await host.StopRoleAsync(submission.RunId);
        }
        catch (InvalidOperationException)
        {
            firstStopFailed = true;
        }

        Assert(firstStopFailed, "a rejected stop should surface as a retryable failure");
        Assert(host.GetLifecycleController(submission.RunId)!.State == ServerRoleLifecycleState.Drained,
            "failed stop should preserve the Drained state and live composition session");
        Assert(sessions.LastState!.ForgetCount == 0 && sessions.LastState.DisposeCount == 0,
            "failed stop must not clean up the candidate registry or session");

        await host.StopRoleAsync(submission.RunId);
        await host.StopRoleAsync(submission.RunId);

        var lifecycle = host.GetLifecycleController(submission.RunId)!;
        Assert(lifecycle.State == ServerRoleLifecycleState.Stopped,
            "successful stop should complete the lifecycle transition");
        Assert(adapter.DrainCount == 1 && adapter.StopCount == 2,
            "explicit drain should not be repeated when stop is retried");
        Assert(sessions.LastState!.ForgetCount == 1 && sessions.LastState.DisposeCount == 1,
            "confirmed stop should forget the runtime and dispose the session exactly once");
        Assert(host.GetRuntimeAssembly(submission.RunId) is null,
            "the host should release its assembly reference after confirmed stop");
    }

    private static async Task ComposedRoleLifecycleCancellationPreservesSession()
    {
        var bundle = new TestBundle(151, "1.0.0", new List<ulong>());
        var definition = new ServerRoleCompositionDefinition(
            "contract.lifecycle-cancel",
            new[] { new MicroBundleManifestEntry(151, "1.0.0") });
        var sessions = new TrackingCompositionSessionFactory(_ => new TestBundleCatalog(bundle));
        var composer = new ServerRoleRuntimeComposer(sessions, new TestRoleCompositionCatalog(definition));
        var adapter = new LifecycleRoleAdapter("contract.lifecycle-cancel") { BlockDrain = true };
        await using var host = new FoundryComposedProvisioningHost(composer, new[] { adapter });
        var submission = host.Submit(CreateManifest("instance-lifecycle-cancel", "contract.lifecycle-cancel"));
        Assert((await host.StartAsync(submission.RunId)).Status == WorkflowStatus.Succeeded,
            "lifecycle cancellation fixture should activate");

        using var cancellation = new CancellationTokenSource();
        var drain = host.DrainRoleAsync(submission.RunId, cancellation.Token).AsTask();
        await adapter.DrainStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        var cancelled = false;
        try
        {
            await drain.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Assert(cancelled, "cancelling a blocked drain should propagate cancellation");
        Assert(host.GetLifecycleController(submission.RunId)!.State == ServerRoleLifecycleState.Running,
            "cancelled drain should restore the Running state");
        Assert(sessions.LastState!.ForgetCount == 0 && sessions.LastState.DisposeCount == 0,
            "cancelled drain must preserve the live runtime registry and composition session");

        adapter.BlockDrain = false;
        await host.StopRoleAsync(submission.RunId);
        Assert(host.GetLifecycleController(submission.RunId)!.State == ServerRoleLifecycleState.Stopped,
            "the role should remain stoppable after a cancelled drain");
    }

    private static async Task ConcurrentDrainAndStopSerialize()
    {
        var bundle = new TestBundle(152, "1.0.0", new List<ulong>());
        var definition = new ServerRoleCompositionDefinition(
            "contract.lifecycle-concurrent",
            new[] { new MicroBundleManifestEntry(152, "1.0.0") });
        var sessions = new TrackingCompositionSessionFactory(_ => new TestBundleCatalog(bundle));
        var composer = new ServerRoleRuntimeComposer(sessions, new TestRoleCompositionCatalog(definition));
        var adapter = new LifecycleRoleAdapter("contract.lifecycle-concurrent") { BlockDrain = true };
        await using var host = new FoundryComposedProvisioningHost(composer, new[] { adapter });
        var submission = host.Submit(CreateManifest("instance-lifecycle-concurrent", "contract.lifecycle-concurrent"));
        Assert((await host.StartAsync(submission.RunId)).Status == WorkflowStatus.Succeeded,
            "concurrent lifecycle fixture should activate");

        var drain = host.DrainRoleAsync(submission.RunId).AsTask();
        await adapter.DrainStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = host.StopRoleAsync(submission.RunId).AsTask();
        adapter.AllowDrain.TrySetResult();
        await Task.WhenAll(drain, stop).WaitAsync(TimeSpan.FromSeconds(5));

        Assert(host.GetLifecycleController(submission.RunId)!.State == ServerRoleLifecycleState.Stopped,
            "concurrent drain and stop should serialize to a terminal Stopped state");
        Assert(adapter.DrainCount == 1 && adapter.StopCount == 1,
            "concurrent drain and stop should not duplicate either adapter operation");
        Assert(sessions.LastState!.ForgetCount == 1 && sessions.LastState.DisposeCount == 1,
            "concurrent lifecycle requests should perform cleanup exactly once");
    }

    private static async Task CompositionFailurePreventsRoleActivation()
    {
        var missingCatalog = new TestBundleCatalog();
        var definition = new ServerRoleCompositionDefinition(
            "contract.missing",
            new[] { new MicroBundleManifestEntry(404, "1.0.0") });
        var composer = new ServerRoleRuntimeComposer(
            new FsmCosCompositionSessionFactory(_ => missingCatalog),
            new TestRoleCompositionCatalog(definition));
        var adapter = new RecordingRuntimeAdapter("contract.missing", isReady: true);
        await using var host = new FoundryComposedProvisioningHost(composer, new[] { adapter });

        var submission = host.Submit(CreateManifest("instance-compose-fail", "contract.missing"));
        var result = await host.StartAsync(submission.RunId);

        Assert(result.Status == WorkflowStatus.Failed, "missing MicroBundle composition should fail the Foundry workflow");
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
            new FsmCosCompositionSessionFactory(_ => new TestBundleCatalog(bundle)),
            new TestRoleCompositionCatalog(definition));
        var adapter = new RecordingRuntimeAdapter("contract.not-ready", isReady: false);
        await using var host = new FoundryComposedProvisioningHost(composer, new[] { adapter });

        var submission = host.Submit(CreateManifest("instance-role-not-ready", "contract.not-ready"));
        var result = await host.StartAsync(submission.RunId);

        Assert(result.Status == WorkflowStatus.Failed, "role adapter refusal must fail the Foundry workflow");
        Assert(host.GetProvisioningResult(submission.RunId)?.IsReady == false,
            "a composed but unready role must not be reported Ready");
        Assert(host.GetRuntimeAssembly(submission.RunId) is null,
            "a failed activation must not remain advertised as the active runtime assembly");
    }

    private static async Task ConcurrentCandidatesHaveIsolatedCatalogs()
    {
        var definitionV1 = new ServerRoleCompositionDefinition(
            "contract.version-one",
            new[] { new MicroBundleManifestEntry(500, "1.0.0") });
        var definitionV2 = new ServerRoleCompositionDefinition(
            "contract.version-two",
            new[] { new MicroBundleManifestEntry(500, "2.0.0") });
        var definitions = new TestRoleCompositionCatalog(definitionV1, definitionV2);
        var sessions = new FsmCosCompositionSessionFactory(definition =>
        {
            var version = definition.RoleId == "contract.version-one" ? "1.0.0" : "2.0.0";
            var bundle = new TestBundle(500, version, new List<ulong>());
            return new TestBundleCatalog(bundle);
        });
        var composer = new ServerRoleRuntimeComposer(sessions, definitions);
        var adapterV1 = new RecordingRuntimeAdapter("contract.version-one", isReady: true);
        var adapterV2 = new RecordingRuntimeAdapter("contract.version-two", isReady: true);
        await using var host = new FoundryComposedProvisioningHost(composer, new[] { adapterV1, adapterV2 });

        var first = host.Submit(CreateManifest("candidate-one", "contract.version-one"));
        var second = host.Submit(CreateManifest("candidate-two", "contract.version-two"));
        var results = await Task.WhenAll(host.StartAsync(first.RunId), host.StartAsync(second.RunId));

        Assert(results.All(result => result.Status == WorkflowStatus.Succeeded),
            "both concurrent candidates should compose and activate successfully");
        Assert(adapterV1.ReceivedAssembly?.TryGetBundle(500, out var bundleV1) == true &&
               bundleV1?.Descriptor.Version == "1.0.0",
            "the first candidate must resolve its own 1.0.0 catalog entry");
        Assert(adapterV2.ReceivedAssembly?.TryGetBundle(500, out var bundleV2) == true &&
               bundleV2?.Descriptor.Version == "2.0.0",
            "the second candidate must resolve its own 2.0.0 catalog entry");
        Assert(adapterV1.ReceivedAssembly!.RuntimeId != adapterV2.ReceivedAssembly!.RuntimeId,
            "candidate sessions must retain distinct runtime identities");
    }

    private static async Task RepositorySessionRejectsConcurrentPreparation()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("single candidate artifact");
        var address = CreateAddress(590, "1.0.0", bytes);
        var artifact = new MicroBundleArtifact(address, bytes);
        var repository = new BlockingArtifactRepository(artifact);
        var materializers = new ServerRolePrecompiledMicroBundleRegistry(new[]
        {
            new KeyValuePair<MicroBundleArtifactAddress, Func<IMicroBundle>>(
                address,
                () => new PinnedTestBundle(590, "1.0.0", new List<ulong>()))
        });
        var definition = new ServerRoleCompositionDefinition(
            "contract.concurrent-prepare",
            new[] { new MicroBundleManifestEntry(590, "1.0.0") });
        await using var session = new RepositoryBackedCompositionSession(
            definition, repository, repository, materializers);

        var firstPreparation = session.PrepareAsync(CancellationToken.None).AsTask();
        await repository.RetrievalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var concurrentRejected = false;
        try
        {
            await session.PrepareAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            concurrentRejected = true;
        }

        repository.AllowRetrieval.TrySetResult(true);
        await firstPreparation;
        Assert(concurrentRejected,
            "a second preparation must not race the first and replace its candidate catalog");
    }

    private static async Task RepositorySessionPreparesVerifiedPinnedDependencyClosure()
    {
        var loadOrder = new List<ulong>();
        var rootBytes = System.Text.Encoding.UTF8.GetBytes("root artifact bytes");
        var dependencyBytes = System.Text.Encoding.UTF8.GetBytes("dependency artifact bytes");
        var rootAddress = CreateAddress(600, "1.0.0", rootBytes);
        var dependencyAddress = CreateAddress(601, "2.0.0", dependencyBytes);
        var rootArtifact = new MicroBundleArtifact(rootAddress, rootBytes);
        var dependencyArtifact = new MicroBundleArtifact(dependencyAddress, dependencyBytes);
        var repository = new TestArtifactRepository(rootArtifact, dependencyArtifact);
        var materializers = new ServerRolePrecompiledMicroBundleRegistry(new[]
        {
            new KeyValuePair<MicroBundleArtifactAddress, Func<IMicroBundle>>(
                rootAddress,
                () => new PinnedTestBundle(600, "1.0.0", loadOrder, new[] { (601UL, "2.0.0") })),
            new KeyValuePair<MicroBundleArtifactAddress, Func<IMicroBundle>>(
                dependencyAddress,
                () => new PinnedTestBundle(601, "2.0.0", loadOrder))
        });
        var definition = new ServerRoleCompositionDefinition(
            "contract.repository-backed",
            new[] { new MicroBundleManifestEntry(600, "1.0.0") });
        await using var session = new RepositoryBackedCompositionSession(
            definition, repository, repository, materializers);

        await session.PrepareAsync(CancellationToken.None);
        var assembly = session.Compose(new RuntimeManifest(
            9001,
            new[] { new MicroBundleManifestEntry(600, "1.0.0") }));

        Assert(assembly.TryGetBundle(600, out _), "the requested root should be present after verified preparation");
        Assert(assembly.TryGetBundle(601, out _), "the exact pinned transitive dependency should be present");
        Assert(loadOrder.SequenceEqual(new[] { 601UL, 600UL }),
            "repository-backed composition should load the dependency before the root");
    }

    private static async Task LocalFileSystemFactoryPreparesPersistedArtifacts()
    {
        var artifactRoot = Path.Combine(Path.GetTempPath(), "serverapp-artifacts-" + Guid.NewGuid().ToString("N"));
        try
        {
            var loadOrder = new List<ulong>();
            var bytes = System.Text.Encoding.UTF8.GetBytes("persisted local artifact");
            var address = CreateAddress(620, "1.0.0", bytes);
            var artifact = new MicroBundleArtifact(address, bytes);

            var writer = new FileSystemMicroBundleRepository(artifactRoot);
            await writer.PutAsync(artifact);

            var materializers = new ServerRolePrecompiledMicroBundleRegistry(new[]
            {
                new KeyValuePair<MicroBundleArtifactAddress, Func<IMicroBundle>>(
                    address,
                    () => new PinnedTestBundle(620, "1.0.0", loadOrder))
            });
            var factory = new LocalFileSystemCompositionSessionFactory(artifactRoot, materializers);
            var definition = new ServerRoleCompositionDefinition(
                "contract.local-filesystem",
                new[] { new MicroBundleManifestEntry(620, "1.0.0") });
            await using var session = factory.Create(definition);

            await session.PrepareAsync(CancellationToken.None);
            var assembly = session.Compose(new RuntimeManifest(
                9020,
                new[] { new MicroBundleManifestEntry(620, "1.0.0") }));

            Assert(factory.ArtifactRoot == Path.GetFullPath(artifactRoot),
                "the local adapter should expose its normalized host-configured artifact root");
            Assert(assembly.TryGetBundle(620, out _),
                "the local filesystem adapter should retrieve and compose the persisted immutable artifact");
            Assert(loadOrder.SequenceEqual(new[] { 620UL }),
                "the local filesystem adapter should materialize only through the registered precompiled factory");
        }
        finally
        {
            if (Directory.Exists(artifactRoot))
                Directory.Delete(artifactRoot, recursive: true);
        }
    }

    private static async Task RepositorySessionDoesNotPublishPartialClosure()
    {
        var rootBytes = System.Text.Encoding.UTF8.GetBytes("root with missing dependency");
        var rootAddress = CreateAddress(610, "1.0.0", rootBytes);
        var rootArtifact = new MicroBundleArtifact(rootAddress, rootBytes);
        var repository = new TestArtifactRepository(rootArtifact);
        var materializers = new ServerRolePrecompiledMicroBundleRegistry(new[]
        {
            new KeyValuePair<MicroBundleArtifactAddress, Func<IMicroBundle>>(
                rootAddress,
                () => new PinnedTestBundle(610, "1.0.0", new List<ulong>(), new[] { (611UL, "1.0.0") }))
        });
        var definition = new ServerRoleCompositionDefinition(
            "contract.repository-incomplete",
            new[] { new MicroBundleManifestEntry(610, "1.0.0") });
        await using var session = new RepositoryBackedCompositionSession(
            definition, repository, repository, materializers);

        var failed = false;
        try
        {
            await session.PrepareAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            failed = true;
        }

        Assert(failed, "a missing transitive dependency must fail candidate preparation");
        var compositionBlocked = false;
        try
        {
            session.Compose(new RuntimeManifest(9002, new[] { new MicroBundleManifestEntry(610, "1.0.0") }));
        }
        catch (InvalidOperationException)
        {
            compositionBlocked = true;
        }

        Assert(compositionBlocked, "FSM_COS must not see a partially staged candidate catalog");
    }

    private static async Task RepositorySessionRejectsWrongArtifactIdentity()
    {
        var expectedBytes = System.Text.Encoding.UTF8.GetBytes("expected");
        var returnedBytes = System.Text.Encoding.UTF8.GetBytes("mismatch");
        var expectedAddress = CreateAddress(630, "1.0.0", expectedBytes);
        var returnedAddress = CreateAddress(630, "1.0.0", returnedBytes);
        var repository = new WrongAddressArtifactRepository(
            new MicroBundleArtifact(expectedAddress, expectedBytes),
            new MicroBundleArtifact(returnedAddress, returnedBytes));
        var materializers = new ServerRolePrecompiledMicroBundleRegistry(
            Array.Empty<KeyValuePair<MicroBundleArtifactAddress, Func<IMicroBundle>>>());
        var definition = new ServerRoleCompositionDefinition(
            "contract.wrong-artifact-address",
            new[] { new MicroBundleManifestEntry(630, "1.0.0") });
        await using var session = new RepositoryBackedCompositionSession(
            definition, repository, repository, materializers);

        var rejected = false;
        try
        {
            await session.PrepareAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        Assert(rejected, "repository retrieval must reject an artifact whose immutable address differs from the requested address");
    }

    private static async Task RepositorySessionRejectsInvalidMaterialization()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("invalid materializer");
        var address = CreateAddress(640, "1.0.0", bytes);
        var artifact = new MicroBundleArtifact(address, bytes);
        var repository = new TestArtifactRepository(artifact);
        var materializers = new ServerRolePrecompiledMicroBundleRegistry(new[]
        {
            new KeyValuePair<MicroBundleArtifactAddress, Func<IMicroBundle>>(
                address,
                () => new PinnedTestBundle(641, "1.0.0", new List<ulong>()))
        });
        var definition = new ServerRoleCompositionDefinition(
            "contract.invalid-materialization",
            new[] { new MicroBundleManifestEntry(640, "1.0.0") });
        await using var session = new RepositoryBackedCompositionSession(
            definition, repository, repository, materializers);

        var rejected = false;
        try
        {
            await session.PrepareAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        Assert(rejected, "a precompiled factory must not materialize a bundle with an identity different from its artifact address");
    }

    private static async Task RepositorySessionRejectsUnpinnedTransitiveDependencies()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("unpinned dependency root");
        var address = CreateAddress(650, "1.0.0", bytes);
        var artifact = new MicroBundleArtifact(address, bytes);
        var repository = new TestArtifactRepository(artifact);
        var materializers = new ServerRolePrecompiledMicroBundleRegistry(new[]
        {
            new KeyValuePair<MicroBundleArtifactAddress, Func<IMicroBundle>>(
                address,
                () => new TestBundle(650, "1.0.0", new List<ulong>(), new[] { 651UL }))
        });
        var definition = new ServerRoleCompositionDefinition(
            "contract.unpinned-dependency",
            new[] { new MicroBundleManifestEntry(650, "1.0.0") });
        await using var session = new RepositoryBackedCompositionSession(
            definition, repository, repository, materializers);

        var rejected = false;
        try
        {
            await session.PrepareAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        Assert(rejected, "repository-backed preparation must reject unpinned transitive dependencies");
    }

    private static async Task RepositorySessionCancellationDoesNotPublish()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("cancel during retrieval");
        var address = CreateAddress(660, "1.0.0", bytes);
        var artifact = new MicroBundleArtifact(address, bytes);
        var repository = new BlockingArtifactRepository(artifact);
        var materializers = new ServerRolePrecompiledMicroBundleRegistry(new[]
        {
            new KeyValuePair<MicroBundleArtifactAddress, Func<IMicroBundle>>(
                address,
                () => new PinnedTestBundle(660, "1.0.0", new List<ulong>()))
        });
        var definition = new ServerRoleCompositionDefinition(
            "contract.cancel-preparation",
            new[] { new MicroBundleManifestEntry(660, "1.0.0") });
        await using var session = new RepositoryBackedCompositionSession(
            definition, repository, repository, materializers);
        using var cancellation = new CancellationTokenSource();

        var preparation = session.PrepareAsync(cancellation.Token).AsTask();
        await repository.RetrievalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        var cancelled = false;
        try
        {
            await preparation.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Assert(cancelled, "cancellation during repository retrieval must abort preparation");
        var compositionBlocked = false;
        try
        {
            session.Compose(new RuntimeManifest(9060, new[] { new MicroBundleManifestEntry(660, "1.0.0") }));
        }
        catch (InvalidOperationException)
        {
            compositionBlocked = true;
        }

        Assert(compositionBlocked, "cancelled preparation must not publish a partial candidate catalog");
    }

    private static async Task RepositorySessionDisposalWinsAgainstLatePreparation()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("dispose during retrieval");
        var address = CreateAddress(670, "1.0.0", bytes);
        var artifact = new MicroBundleArtifact(address, bytes);
        var repository = new BlockingArtifactRepository(artifact);
        var materializers = new ServerRolePrecompiledMicroBundleRegistry(new[]
        {
            new KeyValuePair<MicroBundleArtifactAddress, Func<IMicroBundle>>(
                address,
                () => new PinnedTestBundle(670, "1.0.0", new List<ulong>()))
        });
        var definition = new ServerRoleCompositionDefinition(
            "contract.dispose-during-preparation",
            new[] { new MicroBundleManifestEntry(670, "1.0.0") });
        var session = new RepositoryBackedCompositionSession(
            definition, repository, repository, materializers);

        var preparation = session.PrepareAsync(CancellationToken.None).AsTask();
        await repository.RetrievalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.DisposeAsync();
        repository.AllowRetrieval.TrySetResult(true);

        var disposed = false;
        try
        {
            await preparation.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (ObjectDisposedException)
        {
            disposed = true;
        }

        Assert(disposed, "a preparation completing after disposal must not publish into the disposed session");
        var compositionRejected = false;
        try
        {
            session.Compose(new RuntimeManifest(9070, new[] { new MicroBundleManifestEntry(670, "1.0.0") }));
        }
        catch (ObjectDisposedException)
        {
            compositionRejected = true;
        }

        Assert(compositionRejected, "a disposed session must reject composition after late preparation completes");
        await session.DisposeAsync();
    }

    private static MicroBundleArtifactAddress CreateAddress(ulong bundleId, string version, byte[] bytes) =>
        new(bundleId, version, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());

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

    private sealed class BlockingArtifactRepository(MicroBundleArtifact artifact)
        : IMicroBundleRepository, IMicroBundleRepositoryObserver
    {
        public TaskCompletionSource<bool> RetrievalStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> AllowRetrieval { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<MicroBundleArtifact?> GetAsync(
            MicroBundleArtifactAddress address,
            CancellationToken cancellationToken = default)
        {
            RetrievalStarted.TrySetResult(true);
            await AllowRetrieval.Task.WaitAsync(cancellationToken);
            return artifact.Address == address ? artifact : null;
        }

        public ValueTask PutAsync(MicroBundleArtifact value, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The contract fixture is read-only.");

        public ValueTask<IReadOnlyList<MicroBundleArtifactObservation>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<MicroBundleArtifactObservation> observations = new[]
            {
                new MicroBundleArtifactObservation(artifact.Address, artifact.Content.Length, null)
            };
            return ValueTask.FromResult(observations);
        }
    }

    private sealed class WrongAddressArtifactRepository(
        MicroBundleArtifact advertised,
        MicroBundleArtifact returned)
        : IMicroBundleRepository, IMicroBundleRepositoryObserver
    {
        public ValueTask<MicroBundleArtifact?> GetAsync(
            MicroBundleArtifactAddress address,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<MicroBundleArtifact?>(returned);
        }

        public ValueTask PutAsync(MicroBundleArtifact artifact, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The contract fixture is read-only.");

        public ValueTask<IReadOnlyList<MicroBundleArtifactObservation>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<MicroBundleArtifactObservation> observations = new[]
            {
                new MicroBundleArtifactObservation(advertised.Address, advertised.Content.Length, null)
            };
            return ValueTask.FromResult(observations);
        }
    }

    private sealed class TestArtifactRepository(params MicroBundleArtifact[] artifacts)
        : IMicroBundleRepository, IMicroBundleRepositoryObserver
    {
        private readonly IReadOnlyDictionary<MicroBundleArtifactAddress, MicroBundleArtifact> _artifacts =
            artifacts.ToDictionary(artifact => artifact.Address);

        public ValueTask<MicroBundleArtifact?> GetAsync(
            MicroBundleArtifactAddress address,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _artifacts.TryGetValue(address, out var artifact);
            return ValueTask.FromResult(artifact);
        }

        public ValueTask PutAsync(MicroBundleArtifact artifact, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The contract fixture is read-only.");

        public ValueTask<IReadOnlyList<MicroBundleArtifactObservation>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<MicroBundleArtifactObservation> observations = _artifacts.Values
                .Select(artifact => new MicroBundleArtifactObservation(
                    artifact.Address, artifact.Content.Length, null))
                .ToArray();
            return ValueTask.FromResult(observations);
        }
    }

    private sealed class PinnedTestBundle : IMicroBundle
    {
        private readonly IList<ulong> _loadOrder;

        public PinnedTestBundle(
            ulong id,
            string version,
            IList<ulong> loadOrder,
            IEnumerable<(ulong Id, string Version)>? dependencies = null)
        {
            _loadOrder = loadOrder;
            var pins = (dependencies ?? Array.Empty<(ulong, string)>()).ToArray();
            Descriptor = new MicroBundleDescriptor(
                id,
                version,
                dependencies: pins.Select(pin => MicroBundleDependency.Pinned(pin.Id, pin.Version)));
            Dependencies = pins
                .Select(pin => MicroBundleDependencyRequest.Pinned(pin.Id, pin.Version))
                .ToArray();
        }

        public MicroBundleDescriptor Descriptor { get; }
        public IReadOnlyList<MicroBundleDependencyRequest> Dependencies { get; }
        public void Load(IMicroBundleLoadContext context) => _loadOrder.Add(Id);
        public ulong Id => Descriptor.Id;
        public bool Arbitrate(IMicroBundleArbitrationContext context, int roundIndex) => false;
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

    private sealed class TrackingCompositionSessionFactory(
        Func<ServerRoleCompositionDefinition, IMicroBundleCatalog> catalogFactory)
        : IServerRoleCompositionSessionFactory
    {
        private readonly FsmCosCompositionSessionFactory _inner = new(catalogFactory);
        public TrackingCompositionSessionState? LastState { get; private set; }

        public IServerRoleCompositionSession Create(ServerRoleCompositionDefinition definition)
        {
            LastState = new TrackingCompositionSessionState();
            return new TrackingCompositionSession(_inner.Create(definition), LastState);
        }
    }

    private sealed class TrackingCompositionSessionState
    {
        public int ForgetCount { get; set; }
        public int DisposeCount { get; set; }
    }

    private sealed class TrackingCompositionSession(
        IServerRoleCompositionSession inner,
        TrackingCompositionSessionState state) : IServerRoleCompositionSession
    {
        public ValueTask PrepareAsync(CancellationToken cancellationToken) =>
            inner.PrepareAsync(cancellationToken);
        public RuntimeAssembly Compose(RuntimeManifest manifest) => inner.Compose(manifest);
        public bool TrySelect(ulong runtimeId) => inner.TrySelect(runtimeId);
        public bool Forget(ulong runtimeId)
        {
            state.ForgetCount++;
            return inner.Forget(runtimeId);
        }
        public bool TryGet(ulong runtimeId, out RuntimeAssembly? assembly) =>
            inner.TryGet(runtimeId, out assembly);
        public async ValueTask DisposeAsync()
        {
            state.DisposeCount++;
            await inner.DisposeAsync();
        }
    }

    private sealed class LifecycleRoleAdapter(string roleId) : IServerRoleLifecycleAdapter
    {
        public string RoleId { get; } = roleId;
        public int DrainCount { get; private set; }
        public int StopCount { get; private set; }
        public bool FailFirstStop { get; set; }
        public bool BlockDrain { get; set; }
        public TaskCompletionSource DrainStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowDrain { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<RoleStartResult> StartAsync(
            ServerRoleManifest manifest, RuntimeAssembly assembly, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new RoleStartResult(true, "Lifecycle fixture is ready."));
        }

        public async ValueTask<RoleLifecycleResult> DrainAsync(
            ServerRoleManifest manifest, RuntimeAssembly assembly, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DrainCount++;
            DrainStarted.TrySetResult();
            if (BlockDrain)
                await AllowDrain.Task.WaitAsync(cancellationToken);
            return new RoleLifecycleResult(true, "Lifecycle fixture drained.");
        }

        public ValueTask<RoleLifecycleResult> StopAsync(
            ServerRoleManifest manifest, RuntimeAssembly assembly, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            if (FailFirstStop && StopCount == 1)
                return ValueTask.FromResult(new RoleLifecycleResult(false, "Transient stop failure."));
            return ValueTask.FromResult(new RoleLifecycleResult(true, "Lifecycle fixture stopped."));
        }
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
