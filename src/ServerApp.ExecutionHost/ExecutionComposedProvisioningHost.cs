using System.Collections.Concurrent;
using System.Text.Json;
using ServerApp.Core;
using TheSingularityWorkshop.FSM_COS;
using TheSingularityWorkshop.Execution;

namespace ServerApp.ExecutionHost;

/// <summary>
/// Runs a complete role request through Execution's workflow lifecycle:
/// validate -> FSM_COS compose -> host role activation.
/// Live RuntimeAssembly references remain in host-owned operation state; Execution's bounded
/// JSON workflow context carries only an opaque operation correlation ID.
/// </summary>
public sealed class ExecutionComposedProvisioningHost : IAsyncDisposable
{
    private const string OperationIdInput = "serverapp-operation-id";
    private const string ValidateHandler = "serverapp.validate-role";
    private const string ComposeHandler = "serverapp.compose-runtime";
    private const string ActivateHandler = "serverapp.activate-role";

    private readonly ServerRoleRuntimeComposer _composer;
    private readonly IServerRoleExecutionObserver? _observer;
    private readonly IReadOnlyDictionary<string, IServerRoleRuntimeAdapter> _adapters;
    private readonly WorkflowExecutionCoordinator _workflows;
    private readonly ConcurrentDictionary<Guid, OperationState> _operations = new();
    private readonly ConcurrentDictionary<Guid, Guid> _runOperations = new();
    private readonly ConcurrentDictionary<string, Guid> _pendingInstances = new(StringComparer.Ordinal);

    public ExecutionComposedProvisioningHost(
        ServerRoleRuntimeComposer composer,
        IEnumerable<IServerRoleRuntimeAdapter> adapters,
        IServerRoleExecutionObserver? observer = null)
    {
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _observer = observer;
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

    /// <summary>Accepts a provisioning operation into Execution without executing it.</summary>
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
            state.RunId = snapshot.RunId;
            _runOperations.TryAdd(snapshot.RunId, operationId);
            Observe(snapshot.RunId, state, "workflow", "accepted", detail: "Execution accepted the provisioning run.");
            return snapshot;
        }
        catch
        {
            _operations.TryRemove(operationId, out _);
            _pendingInstances.TryRemove(manifest.InstanceId, out _);
            throw;
        }
    }

    /// <summary>Starts one accepted Execution run and returns its authoritative workflow result.</summary>
    public async Task<WorkflowResult> StartAsync(Guid runId, TimeSpan? timeout = null)
    {
        try
        {
            if (TryGetOperation(runId, out var startedState))
                Observe(runId, startedState, "workflow", "started");
            var result = await _workflows.StartAsync(runId, timeout).ConfigureAwait(false);
            if (TryGetOperation(runId, out var completedState))
                Observe(runId, completedState, "workflow", result.Status.ToString().ToLowerInvariant());
            return result;
        }
        catch (Exception exception)
        {
            if (TryGetOperation(runId, out var failedState))
                Observe(runId, failedState, "workflow", "exception", detail: exception.GetType().Name);
            throw;
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
                        state.Result = Cancelled(state.Manifest, "Execution cancelled the provisioning workflow.");

                    // A successfully activated role still owns this InstanceId. Keep the
                    // reservation until a host lifecycle/stop path explicitly releases it.
                    if (snapshot.Status != WorkflowRunStatus.Succeeded)
                        ReleasePendingInstance(runId);
                }
            }
            catch (KeyNotFoundException)
            {
                // Preserve the original StartAsync exception for an unknown run ID.
            }
        }
    }

    /// <summary>Requests cancellation through Execution; running handlers must cooperate.</summary>
    public bool Cancel(Guid runId)
    {
        var cancelled = _workflows.Cancel(runId);
        if (cancelled && _workflows.GetSnapshot(runId).Status == WorkflowRunStatus.Cancelled)
        {
            if (TryGetOperation(runId, out var state))
            {
                Observe(runId, state, "workflow", "cancelled", detail: "Execution accepted cancellation.");
                if (state.Result is null)
                    state.Result = Cancelled(state.Manifest, "Execution cancelled the accepted provisioning workflow.");
            }
            ReleasePendingInstance(runId);
        }
        return cancelled;
    }

    public WorkflowRunSnapshot GetSnapshot(Guid runId) => _workflows.GetSnapshot(runId);

    public ProvisioningResult? GetProvisioningResult(Guid runId) =>
        TryGetOperation(runId, out var state) ? state.Result : null;

    public RuntimeAssembly? GetRuntimeAssembly(Guid runId) =>
        TryGetOperation(runId, out var state) ? state.Assembly : null;

    /// <summary>Returns the host-owned lifecycle controller for a successfully activated role.</summary>
    public ServerRoleLifecycleController? GetLifecycleController(Guid runId) =>
        TryGetOperation(runId, out var state) ? state.Lifecycle : null;

    /// <summary>Stops new work for an active role and waits for its adapter to drain.</summary>
    public ValueTask DrainRoleAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var lifecycle = GetLifecycleController(runId)
            ?? throw new InvalidOperationException("The run has no active role with lifecycle support.");
        return lifecycle.DrainAsync(cancellationToken);
    }

    /// <summary>
    /// Stops an active role. The instance reservation and FSM_COS registry entry are retained
    /// until the adapter explicitly confirms stop.
    /// </summary>
    public async ValueTask StopRoleAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        if (!TryGetOperation(runId, out var state) || state.Lifecycle is null)
            throw new InvalidOperationException("The run has no active role with lifecycle support.");

        await state.Lifecycle.StopAsync(cancellationToken).ConfigureAwait(false);
        if (state.Lifecycle.State == ServerRoleLifecycleState.Stopped)
        {
            state.Assembly = null;
            ReleasePendingInstance(runId);
        }
    }

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
            Observe(context, state, "composition", "started");
            state.Assembly = _composer.Compose(definition);
            foreach (var bundle in state.Assembly.LoadedBundles)
                Observe(context, state, "bundle", "loaded", state.Assembly.RuntimeId, bundle.Id, bundle.Descriptor.Version);
            Observe(context, state, "composition", "succeeded", state.Assembly.RuntimeId);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Observe(context, state, "composition", "cancelled", state.Assembly?.RuntimeId);
            if (state.Assembly is not null)
                _composer.Forget(state.Assembly.RuntimeId);
            throw;
        }
        catch (Exception exception)
        {
            Observe(context, state, "composition", "failed", state.Assembly?.RuntimeId, detail: exception.GetType().Name);
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
            Observe(context, state, "role-adapter", "invocation-started", assembly.RuntimeId, detail: adapter.RoleId);
            RoleStartResult start;
            try
            {
                start = await adapter.StartAsync(state.Manifest, assembly, cancellationToken).ConfigureAwait(false);
                Observe(context, state, "role-adapter", start.IsReady ? "completed-ready" : "completed-not-ready", assembly.RuntimeId, detail: adapter.RoleId);
            }
            catch (Exception exception)
            {
                Observe(context, state, "role-adapter", "failed", assembly.RuntimeId, detail: $"{adapter.RoleId}:{exception.GetType().Name}");
                throw;
            }
            if (!start.IsReady)
                throw new InvalidOperationException(start.Detail);

            state.Result = new ProvisioningResult(
                state.Manifest.InstanceId,
                state.Manifest.RoleId,
                ServerInstanceState.Ready,
                start.Detail,
                Array.Empty<ManifestValidationError>());
            if (adapter is IServerRoleLifecycleAdapter lifecycleAdapter)
                state.Lifecycle = new ServerRoleLifecycleController(
                    state.Manifest, assembly, lifecycleAdapter, _composer);
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

    private void Observe(WorkflowExecutionContext context, OperationState state, string phase, string status,
        ulong? runtimeId = null, ulong? bundleId = null, string? bundleVersion = null, string? detail = null) =>
        Observe(state.RunId, state, phase, status, runtimeId, bundleId, bundleVersion, detail);

    private void Observe(Guid runId, OperationState state, string phase, string status,
        ulong? runtimeId = null, ulong? bundleId = null, string? bundleVersion = null, string? detail = null)
    {
        if (_observer is null || !_runOperations.ContainsKey(runId))
            return;

        var observation = new ServerRoleExecutionObservation(
            runId, Interlocked.Increment(ref state.ObservationSequence), phase, status,
            DateTimeOffset.UtcNow, runtimeId, bundleId, bundleVersion, detail);
        try
        {
            _observer.OnObservation(observation);
        }
        catch
        {
            // Telemetry must never change the provisioning result or suppress cleanup.
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

    public async ValueTask DisposeAsync()
    {
        await _workflows.DisposeAsync().ConfigureAwait(false);
        foreach (var state in _operations.Values)
        {
            if (state.Lifecycle is null || state.Lifecycle.State == ServerRoleLifecycleState.Stopped)
                continue;

            await state.Lifecycle.StopAsync(CancellationToken.None).ConfigureAwait(false);
            if (state.Lifecycle.State == ServerRoleLifecycleState.Stopped)
            {
                state.Assembly = null;
                ReleasePendingInstance(state.RunId);
            }
        }
    }

    private sealed class OperationState(ServerRoleManifest manifest)
    {
        public ServerRoleManifest Manifest { get; } = manifest;
        public Guid RunId { get; set; }
        public ServerRoleCompositionDefinition? Definition { get; set; }
        public RuntimeAssembly? Assembly { get; set; }
        public ServerRoleLifecycleController? Lifecycle { get; set; }
        public ProvisioningResult? Result { get; set; }
        public long ObservationSequence;
    }
}
