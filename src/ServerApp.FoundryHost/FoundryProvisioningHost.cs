using System.Collections.Concurrent;
using System.Text.Json;
using ServerApp.Core;
using TheSingularityWorkshop.Foundry;

namespace ServerApp.FoundryHost;

/// <summary>
/// Hosts ServerApp role provisioning inside Foundry's real workflow lifecycle.
/// This is a workflow adapter; it does not compose FSM_COS RuntimeAssembly instances.
/// </summary>
public sealed class FoundryProvisioningHost : IAsyncDisposable
{
    private const string ProvisionHandlerKey = "serverapp.provision-role";
    private const string ManifestInputKey = "server-role-manifest";
    private const string ProvisioningOutputKey = "provisioning-result";

    private readonly ProvisioningCoordinator _provisioner;
    private readonly WorkflowExecutionCoordinator _workflows;
    private readonly ConcurrentDictionary<string, Guid> _pendingInstances = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, string> _runInstances = new();

    public FoundryProvisioningHost(IEnumerable<IServerRoleAdapter> roleAdapters)
    {
        _provisioner = new ProvisioningCoordinator(roleAdapters);
        var handlers = new WorkflowHandlerRegistry();
        handlers.Register(ProvisionHandlerKey, ProvisionRoleAsync);
        _workflows = new WorkflowExecutionCoordinator(handlers);
    }

    /// <summary>Accepts one role request into Foundry without starting it.</summary>
    public WorkflowRunSnapshot Submit(ServerRoleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!_pendingInstances.TryAdd(manifest.InstanceId, Guid.Empty))
            throw new InvalidOperationException(
                $"A provisioning workflow for instance '{manifest.InstanceId}' is already accepted or running.");

        try
        {
            var context = new WorkflowExecutionContext(
                new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    [ManifestInputKey] = JsonSerializer.SerializeToElement(manifest)
                });
            var definition = new WorkflowDefinition(
                "serverapp.provision-role",
                $"Provision {manifest.RoleId} ({manifest.InstanceId})",
                WorkflowDefinition.SupportedSchemaVersion,
                new[] { new WorkflowStep("provision-role", ProvisionHandlerKey) });

            var snapshot = _workflows.Submit(definition, context);
            _pendingInstances[manifest.InstanceId] = snapshot.RunId;
            _runInstances[snapshot.RunId] = manifest.InstanceId;
            return snapshot;
        }
        catch
        {
            _pendingInstances.TryRemove(manifest.InstanceId, out _);
            throw;
        }
    }

    /// <summary>Starts one accepted Foundry run and returns its authoritative workflow result.</summary>
    public async Task<WorkflowResult> StartAsync(Guid runId, TimeSpan? timeout = null)
    {
        try
        {
            return await _workflows.StartAsync(runId, timeout).ConfigureAwait(false);
        }
        finally
        {
            ReleaseInstance(runId);
        }
    }

    /// <summary>Requests cooperative cancellation through Foundry.</summary>
    public bool Cancel(Guid runId)
    {
        var cancelled = _workflows.Cancel(runId);
        if (cancelled && _workflows.GetSnapshot(runId).Status == WorkflowRunStatus.Cancelled)
            ReleaseInstance(runId);
        return cancelled;
    }

    public WorkflowRunSnapshot GetSnapshot(Guid runId) => _workflows.GetSnapshot(runId);

    /// <summary>Reads the Core provisioning result produced by the registered workflow handler.</summary>
    public ProvisioningResult? GetProvisioningResult(Guid runId, WorkflowExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.TryGetOutput(ProvisioningOutputKey, out ProvisioningResult? result)
            ? result
            : null;
    }

    private async ValueTask ProvisionRoleAsync(
        WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        var manifest = context.GetInput<ServerRoleManifest>(ManifestInputKey);
        var result = await _provisioner.ProvisionAsync(manifest, cancellationToken).ConfigureAwait(false);
        context.SetOutput(ProvisioningOutputKey, result);

        if (!result.IsReady)
            throw new InvalidOperationException(
                $"Server role '{result.RoleId}' instance '{result.InstanceId}' did not become Ready.");
    }

    private void ReleaseInstance(Guid runId)
    {
        if (_runInstances.TryRemove(runId, out var instanceId))
            _pendingInstances.TryRemove(instanceId, out _);
    }

    public ValueTask DisposeAsync() => _workflows.DisposeAsync();
}
