using ServerApp.Core;
using ServerApp.FoundryHost;
using TheSingularityWorkshop.Foundry;

await SuccessfulRoleActivationIsAFoundryWorkflow();
await FailedRoleActivationFailsTheFoundryWorkflow();
await DuplicatePendingInstanceIsRejected();
await AcceptedWorkflowCanBeCancelled();
await RunningWorkflowCanBeCancelled();
Console.WriteLine("All ServerApp Foundry host contract checks passed.");

static async Task SuccessfulRoleActivationIsAFoundryWorkflow()
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

static async Task FailedRoleActivationFailsTheFoundryWorkflow()
{
    await using var host = new FoundryProvisioningHost(new[] { new NotReadyRoleAdapter() });
    var submission = host.Submit(CreateManifest("instance-failed"));
    var result = await host.StartAsync(submission.RunId);

    Assert(result.Status == WorkflowStatus.Failed, "a role that does not become Ready must fail its Foundry workflow");
    Assert(host.GetProvisioningResult(submission.RunId)?.State == ServerInstanceState.Failed,
        "the structured ServerApp failure result should remain observable");
}

static async Task DuplicatePendingInstanceIsRejected()
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

static async Task AcceptedWorkflowCanBeCancelled()
{
    await using var host = new FoundryProvisioningHost(new[] { new ReadyRoleAdapter() });
    var submission = host.Submit(CreateManifest("instance-cancelled"));
    Assert(host.Cancel(submission.RunId), "an accepted workflow should be cancellable before start");
    Assert(host.GetSnapshot(submission.RunId).Status == WorkflowRunStatus.Cancelled,
        "Foundry should record the accepted run as cancelled");
}

static async Task RunningWorkflowCanBeCancelled()
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

static ServerRoleManifest CreateManifest(string instanceId) => new()
{
    SchemaVersion = 1,
    InstanceId = instanceId,
    RoleId = "contract.ready",
    Dependencies = Array.Empty<string>(),
    Resources = new ResourceRequirements { CpuCores = 1, MemoryMiB = 64 }
};

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

sealed class ReadyRoleAdapter : IServerRoleAdapter
{
    public string RoleId => "contract.ready";

    public ValueTask<RoleStartResult> StartAsync(
        ServerRoleManifest manifest, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new RoleStartResult(true, "Contract fixture reports ready."));
    }
}

sealed class BlockingRoleAdapter : IServerRoleAdapter
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

sealed class NotReadyRoleAdapter : IServerRoleAdapter
{
    public string RoleId => "contract.ready";

    public ValueTask<RoleStartResult> StartAsync(
        ServerRoleManifest manifest, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new RoleStartResult(false, "Contract fixture refused readiness."));
}
