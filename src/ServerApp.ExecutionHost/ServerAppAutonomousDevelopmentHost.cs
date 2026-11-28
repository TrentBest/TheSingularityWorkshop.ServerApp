using TheSingularityWorkshop.Execution;

namespace ServerApp.ExecutionHost;

/// <summary>
/// Composes ServerApp's durable task host, CiAi V1 evidence lifecycle gate,
/// configured HeadlessAi turn coordinator, host-owned operation policy/executor,
/// and durable human approval queue into one bounded autonomous-development entry point.
/// </summary>
/// <remarks>
/// The persistent task coordinator and evidence lifecycle coordinator must share the same
/// JsonlWorkTaskRepairAttemptJournal instance. Construct that journal once and inject it
/// into both components; separate instances over one path do not share an in-memory view.
/// </remarks>
public sealed class ServerAppAutonomousDevelopmentHost
{
    private readonly PersistentWorkTaskCoordinator _tasks;
    private readonly ServerAppCiAiEvidenceLifecycleCoordinator _evidenceLifecycle;

    public ServerAppAutonomousDevelopmentHost(
        PersistentWorkTaskCoordinator tasks,
        ExecutionHeadlessAiTurnCoordinator headlessAiTurns,
        WorkTaskCapabilityRegistry capabilities,
        IExecutionAgentOperationPolicy operationPolicy,
        IExecutionAgentOperationExecutor operationExecutor,
        HumanApprovalQueueHost approvals,
        ServerAppCiAiEvidenceLifecycleCoordinator evidenceLifecycle)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _evidenceLifecycle = evidenceLifecycle ?? throw new ArgumentNullException(nameof(evidenceLifecycle));
        if (!_evidenceLifecycle.IsBoundTo(_tasks))
            throw new ArgumentException("Evidence lifecycle coordinator must use the same persistent task coordinator.", nameof(evidenceLifecycle));
        ArgumentNullException.ThrowIfNull(approvals);
        var router = new ExecutionAgentOperationRouter(
            capabilities, operationPolicy, operationExecutor, approvals.Store);
        AgentTurns = new ExecutionAgentTurnOrchestrator(tasks, headlessAiTurns, router);
    }

    /// <summary>One configured LLM turn and its ordered operation disposition.</summary>
    public ExecutionAgentTurnOrchestrator AgentTurns { get; }

    /// <summary>
    /// Applies one accepted, independently revalidated ciai.workflow-evidence.v1 packet
    /// to the durable task lifecycle. This is the only ServerApp entry point that may
    /// create ReviewReady, RepairRequired, Blocked, or CODE RED from CI evidence.
    /// </summary>
    public WorkTaskSnapshot ApplyAuthoritativeWorkflowEvidence(
        string taskId,
        Guid leaseId,
        CiAiRequestTaskMappingSnapshot request,
        ExecutionAcceptedCiAiWorkflowEvidence acceptedEvidence,
        TimeSpan maximumRepairDuration,
        DateTimeOffset now) =>
        _evidenceLifecycle.ApplyAuthoritativeEvidence(
            taskId, leaseId, request, acceptedEvidence, maximumRepairDuration, now);

    public WorkTaskSnapshot ApplyAuthoritativeWorkflowEvidence(
        string taskId,
        Guid leaseId,
        ServerAppAcceptedCiAiEvidence acceptedEvidence,
        TimeSpan maximumRepairDuration,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(acceptedEvidence);
        return _evidenceLifecycle.ApplyAuthoritativeEvidence(
            taskId, leaseId, acceptedEvidence.Request, acceptedEvidence.WorkflowEvidence,
            maximumRepairDuration, now);
    }

    /// <summary>
    /// Consumes a retrieved and byte-verified ciai.test-results.v1 artifact through the
    /// persistent Execution coordinator, then starts a repair turn only if that report
    /// matches the exact authoritative V1 failure that already placed the task in RepairRequired.
    /// The diagnostic report cannot create or alter lifecycle state.
    /// </summary>
    public Task<ExecutionAgentTurnOutcome> ProcessCiAiDiagnosticReportAsync(
        string taskId,
        Guid leaseId,
        string authorizedBranch,
        string candidateCommitSha,
        Guid capabilityId,
        ServerAppAcceptedCiAiEvidence acceptedEvidence,
        ExecutionVerifiedCiAiReportArtifact verifiedArtifact,
        DateTimeOffset now,
        string priorTurnId,
        string priorCandidateCommitSha,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acceptedEvidence);
        ArgumentNullException.ThrowIfNull(verifiedArtifact);
        var acceptedMapping = acceptedEvidence.Request.Mapping.Validate();
        var reportMapping = verifiedArtifact.Request.Mapping.Validate();
        if (!string.Equals(acceptedMapping.RequestId, reportMapping.RequestId, StringComparison.Ordinal) ||
            !string.Equals(acceptedMapping.OperationId, reportMapping.OperationId, StringComparison.Ordinal) ||
            !string.Equals(acceptedMapping.CandidateSha, reportMapping.CandidateSha, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Diagnostic report notice is not bound to the same accepted workflow-evidence dispatch.");
        ArgumentException.ThrowIfNullOrWhiteSpace(priorTurnId);
        ArgumentException.ThrowIfNullOrWhiteSpace(priorCandidateCommitSha);

        var diagnostic = _tasks.ConsumeCiAiDiagnosticReport(
            taskId, leaseId, verifiedArtifact, acceptedEvidence.SourceArtifactBinding, now);
        if (diagnostic.Verdict != "ActionableFailure")
            throw new UnauthorizedAccessException("HeadlessAi repair turns require a matching actionable CiAi diagnostic report.");

        var instructions =
            "Analyze the accepted CiAi diagnostic report for the exact failed candidate and propose a bounded repair. " +
            "Use only the supplied evidence and registered operation vocabulary. " +
            "Do not claim that a proposed operation was executed or authorized.";
        var canonicalPrompt =
            "ADP accepted diagnostic report\n" +
            "Task: " + taskId + "\n" +
            "Report identity: " + diagnostic.AcceptedReport.ReportIdentity + "\n" +
            "Report SHA-256: " + diagnostic.AcceptedReport.ReportSha256 + "\n" +
            "Authoritative workflow-evidence SHA-256: " +
            diagnostic.AcceptedReport.AuthoritativeWorkflowEvidenceSha256 + "\n" +
            "The exact report bytes and nested source-artifact identity are supplied separately as accepted context.";
        return AgentTurns.InvokeRepairAndRouteAsync(
            taskId, leaseId, authorizedBranch, candidateCommitSha, capabilityId,
            "repair", instructions, canonicalPrompt, ExecutionHeadlessAiResponseSchema.JsonSchema,
            priorTurnId, priorCandidateCommitSha, diagnostic,
            approvalLifetime: TimeSpan.FromDays(1),
            cancellationToken: cancellationToken);
    }
}
