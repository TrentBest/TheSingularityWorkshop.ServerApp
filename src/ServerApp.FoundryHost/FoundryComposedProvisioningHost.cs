using System.Collections.Concurrent;
using System.Text.Json;
using ServerApp.Core;
using TheSingularityWorkshop.FSM_COS;
using TheSingularityWorkshop.Foundry;

namespace ServerApp.FoundryHost;

/// <summary>
/// Runs a complete role request through Foundry's workflow lifecycle:
/// validate -> FSM_COS compose -> host role activation.
/// Live RuntimeAssembly references remain in host-owned operation state; Foundry's bounded
/// JSON workflow context carries only an opaque operation correlation ID.
/// </summary>
public sealed class FoundryComposedProvisioningHost : IAsyncDisposable
{
    private const string OperationIdInput = "serverapp-operation-id";
    private const string ValidateHandler = "serverapp.validate-role";
    private const string ComposeHandler = "serverapp.compose-runtime";
    private const string ActivateHandler = "serverapp.activate-role";

    private readonly ServerRoleRuntimeComposer _composer;
    private readonly IReadOnlyDictionary<string, IServerRoleRuntimeAdapter> _adapters;
    private readonly WorkflowExecutionCoordinator _workflows;
    private readonly ConcurrentDictionary<Guid, OperationState> _operations = new();
    private readonly ConcurrentDictionary<Guid, Guid> _runOperations = new();
    private readonly ConcurrentDictionary<string, Guid> _pendingInstances = new(StringComparer.Ordinal);

    public FoundryComposedProvisioningHost(
        ServerRoleRuntimeComposer composer,
        IEnumerable<IServerRoleRuntimeAdapter> adapters)
    {
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        ArgumentNullException.ThrowIfNull(adapters);

        var map = new Dictionary<string, IServerRoleRuntimeAdapter>(StringComparer.Ordinal);
        foreach (var adapter in adapters)
        {
            ArgumentNullException.ThrowIfNull(adapter);
            if (string.IsNullOrWhiteSpace(adapter.RoleId))
                throw new ArgumentException("Every runtime adapter must declare a non-empty RoleId.", nameof(adapters));
            if (!map.TryAdd(adapter.RoleId, adapter))
                throw new ArgumentException($"Duplicate runtime adapter '{adapter.RoleId}'.", nameof(adapters));
        }

        _adapters = map;
        var handlers = new WorkflowHandlerRegistry();
        handlers.Register(ValidateHandler, ValidateAsync);
        handlers.Register(ComposeHandler, ComposeAsync);
        handlers.Register(ActivateHandler, ActivateAsync);
        _workflows = new WorkflowExecutionCoordinator(handlers);
    }

    /// <summary>Accepts a provisioning operation into Foundry without executing it.</summary>
    public WorkflowRunSnapshot Submit(ServerRoleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(manifest.InstanceId))
            throw new ArgumentException("A non-empty InstanceId is required to correlate provisioning.", nameof(manifest));

        var operationId = Guid.NewGuid();
        if (!_pendingInstances.TryAdd(manifest.InstanceId, operationId))
            throw new InvalidOperationException(
                $"A provisioning workflow for instance '{manifest.InstanceId}' is already accepted or running.");

        var manifestSnapshot = manifest with
        {
            Dependencies = manifest.Dependencies is null
                ? null!
                : Array.AsReadOnly(manifest.Dependencies.ToArray()),
            Resources = manifest.Resources is null ? null! : manifest.Resources with { }
        };
        var state = new OperationState(manifestSnapshot);
        _operations.TryAdd(operationId, state);

        try
        {
            var context = new WorkflowExecutionContext(
                new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    [OperationIdInput] = JsonSerializer.SerializeToElement(operationId)
                });
            var definition = new WorkflowDefinition(
                "serverapp.provision-composed-role",
                $"Compose and provision {manifest.RoleId} ({manifest.InstanceId})",
                WorkflowDefinition.SupportedSchemaVersion,
                new[]
                {
                    new WorkflowStep("validate-role", ValidateHandler),
                    new WorkflowStep("compose-runtime", ComposeHandler),
                    new WorkflowStep("activate-role", ActivateHandler)
                });

            var snapshot = _workflows.Submit(definition, context);
            _runOperations.TryAdd(snapshot.RunId, operationId);
            return snapshot;
        }
        catch
        {
            _operations.TryRemove(operationId, out _);
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
            try
            {
                var snapshot = _workflows.GetSnapshot(runId);
                if (snapshot.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Failed or
                    WorkflowRunStatus.Cancelled or WorkflowRunStatus.TimedOut or WorkflowRunStatus.Invalid)
                {
                    if (snapshot.Status == WorkflowRunStatus.Cancelled &&
                        TryGetOperation(runId, out var state) && state.Result is null)
                        state.Result = Cancelled(state.Manifest, "Foundry cancelled the provisioning workflow.");

                    ReleasePendingInstance(runId);
                }
            }
            catch (KeyNotFoundException)
            {
                // Preserve the original StartAsync exception for an unknown run ID.
            }
        }
    }

    /// <summary>Requests cancellation through Foundry; running handlers must cooperate.</summary>
    public bool Cancel(Guid runId)
    {
        var cancelled = _workflows.Cancel(runId);
        if (cancelled && _workflows.GetSnapshot(runId).Status == WorkflowRunStatus.Cancelled)
        {
            if (TryGetOperation(runId, out var state) && state.Result is null)
                state.Result = Cancelled(state.Manifest, "Foundry cancelled the accepted provisioning workflow.");
            ReleasePendingInstance(runId);
        }
        return cancelled;
    }

    public WorkflowRunSnapshot GetSnapshot(Guid runId) => _workflows.GetSnapshot(runId);

    public ProvisioningResult? GetProvisioningResult(Guid runId) =>
        TryGetOperation(runId, out var state) ? state.Result : null;

    public RuntimeAssembly? GetRuntimeAssembly(Guid runId) =>
        TryGetOperation(runId, out var state) ? state.Assembly : null;

    private ValueTask ValidateAsync(WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = GetOperation(context);
        var errors = ManifestValidator.Validate(state.Manifest);
        if (errors.Count > 0)
        {
            state.Result = new ProvisioningResult(
                state.Manifest.InstanceId,
                state.Manifest.RoleId,
                ServerInstanceState.Failed,
                "Server-role manifest validation failed.",
                errors);
            throw new InvalidOperationException("Server-role manifest validation failed.");
        }

        try
        {
            state.Definition = _composer.ValidateAndResolve(state.Manifest);
            if (!_adapters.ContainsKey(state.Manifest.RoleId))
                throw new InvalidOperationException($"No runtime adapter supports role '{state.Manifest.RoleId}'.");
            return ValueTask.CompletedTask;
        }
        catch (Exception exception)
        {
            state.Result ??= Failed(state.Manifest,
                $"Role composition definition validation failed: {exception.GetType().Name}: {exception.Message}");
            throw;
        }
    }

    private ValueTask ComposeAsync(WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = GetOperation(context);
        var definition = state.Definition
            ?? throw new InvalidOperationException("Role validation did not produce a composition definition.");

        try
        {
            state.Assembly = _composer.Compose(definition);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (state.Assembly is not null)
                _composer.Forget(state.Assembly.RuntimeId);
            throw;
        }
        catch (Exception exception)
        {
            state.Result = Failed(state.Manifest, $"FSM_COS composition failed: {exception.GetType().Name}: {exception.Message}");
            throw;
        }
    }

    private async ValueTask ActivateAsync(WorkflowExecutionContext context, CancellationToken cancellationToken)
    {
        var state = GetOperation(context);
        var assembly = state.Assembly
            ?? throw new InvalidOperationException("FSM_COS composition did not produce a RuntimeAssembly.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_composer.TrySelect(assembly.RuntimeId))
                throw new InvalidOperationException($"FSM_COS runtime {assembly.RuntimeId} could not be selected by the host registry.");

            var adapter = _adapters[state.Manifest.RoleId];
            var start = await adapter.StartAsync(state.Manifest, assembly, cancellationToken).ConfigureAwait(false);
            if (!start.IsReady)
                throw new InvalidOperationException(start.Detail);

            state.Result = new ProvisioningResult(
                state.Manifest.InstanceId,
                state.Manifest.RoleId,
                ServerInstanceState.Ready,
                start.Detail,
                Array.Empty<ManifestValidationError>());
        }
        catch (OperationCanceledException)
        {
            _composer.Forget(assembly.RuntimeId);
            state.Assembly = null;
            state.Result = Cancelled(state.Manifest, "Role activation was cancelled before readiness was confirmed.");
            throw;
        }
        catch (Exception exception)
        {
            _composer.Forget(assembly.RuntimeId);
            state.Assembly = null;
            state.Result = Failed(state.Manifest, $"Role activation failed: {exception.GetType().Name}: {exception.Message}");
            throw;
        }
    }

    private OperationState GetOperation(WorkflowExecutionContext context)
    {
        var operationId = context.GetInput<Guid>(OperationIdInput);
        return _operations.TryGetValue(operationId, out var state)
            ? state
            : throw new InvalidOperationException("The provisioning operation correlation is no longer available.");
    }

    private bool TryGetOperation(Guid runId, out OperationState state)
    {
        if (_runOperations.TryGetValue(runId, out var operationId) &&
            _operations.TryGetValue(operationId, out state!))
            return true;

        state = null!;
        return false;
    }

    private void ReleasePendingInstance(Guid runId)
    {
        if (_runOperations.TryGetValue(runId, out var operationId) &&
            _operations.TryGetValue(operationId, out var state))
            _pendingInstances.TryRemove(state.Manifest.InstanceId, out _);
    }

    private static ProvisioningResult Failed(ServerRoleManifest manifest, string detail) =>
        new(manifest.InstanceId, manifest.RoleId, ServerInstanceState.Failed,
            detail, Array.Empty<ManifestValidationError>());

    private static ProvisioningResult Cancelled(ServerRoleManifest manifest, string detail) =>
        new(manifest.InstanceId, manifest.RoleId, ServerInstanceState.Cancelled,
            detail, Array.Empty<ManifestValidationError>());

    public ValueTask DisposeAsync() => _workflows.DisposeAsync();

    private sealed class OperationState(ServerRoleManifest manifest)
    {
        public ServerRoleManifest Manifest { get; } = manifest;
        public ServerRoleCompositionDefinition? Definition { get; set; }
        public RuntimeAssembly? Assembly { get; set; }
        public ProvisioningResult? Result { get; set; }
    }
}
