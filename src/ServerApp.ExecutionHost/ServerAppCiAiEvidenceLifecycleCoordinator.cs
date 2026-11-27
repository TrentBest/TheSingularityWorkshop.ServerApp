using TheSingularityWorkshop.CiAi;
using TheSingularityWorkshop.Execution;

namespace ServerApp.ExecutionHost;

/// <summary>
/// Converts an independently accepted CiAi V1 packet into Execution's durable repair-attempt
/// record and applies the existing deterministic lifecycle policy. Test-result reports are
/// deliberately not accepted here: they remain diagnostic context only.
/// </summary>
/// <remarks>
/// The supplied attempt journal must be the same object instance used to construct
/// <see cref="PersistentWorkTaskCoordinator"/>. A second journal instance over the same path
/// would have a separate in-memory replay view and cannot safely participate in this composition.
/// </remarks>
public sealed class ServerAppCiAiEvidenceLifecycleCoordinator
{
    private readonly PersistentWorkTaskCoordinator _tasks;
    private readonly JsonlWorkTaskRepairAttemptJournal _attemptJournal;

    public ServerAppCiAiEvidenceLifecycleCoordinator(
        PersistentWorkTaskCoordinator tasks,
        JsonlWorkTaskRepairAttemptJournal attemptJournal)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _attemptJournal = attemptJournal ?? throw new ArgumentNullException(nameof(attemptJournal));
    }

    /// <summary>
    /// Applies only the producer-owned, host-revalidated V1 packet. The exact packet digest,
    /// persisted dispatch mapping, current task lease, candidate, run attempt, and required-check
    /// identity are rebound before the attempt is appended and lifecycle policy is evaluated.
    /// </summary>
    public WorkTaskSnapshot ApplyAuthoritativeEvidence(
        string taskId,
        Guid leaseId,
        CiAiRequestTaskMappingSnapshot request,
        ExecutionAcceptedCiAiWorkflowEvidence acceptedEvidence,
        TimeSpan maximumDuration,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(acceptedEvidence);
        if (leaseId == Guid.Empty)
            throw new ArgumentException("A current task lease is required.", nameof(leaseId));
        if (now == default)
            throw new ArgumentException("Lifecycle decision time is required.", nameof(now));
        if (request.Cancelled)
            throw new InvalidOperationException("Cancelled dispatch intent cannot drive task lifecycle.");

        var mapping = request.Mapping.Validate();
        var runBinding = request.RunBinding
            ?? throw new InvalidOperationException("Authoritative workflow evidence requires a persisted exact-run binding.");
        runBinding.ValidateAgainst(mapping);

        var task = _tasks.GetSnapshot(taskId);
        if (!string.Equals(mapping.TaskId, taskId, StringComparison.Ordinal) ||
            !string.Equals(mapping.CorrelationId, task.Request.CorrelationId, StringComparison.Ordinal) ||
            !string.Equals(mapping.Repository, task.Request.Repository, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("CiAi dispatch mapping does not identify the exact Execution task.");
        if (task.Status is not (WorkTaskStatus.Validating or WorkTaskStatus.CodeRed))
            throw new InvalidOperationException("Authoritative CI evidence can be applied only while the task is validating or under CODE RED review.");
        if (task.Lease is not { } lease || lease.LeaseId != leaseId || lease.ExpiresAt <= now)
            throw new UnauthorizedAccessException("A current, unexpired task lease is required to apply CI evidence.");

        var packet = acceptedEvidence.Packet;
        if (!string.Equals(packet.Schema, CiWorkflowEvidencePacket.CurrentSchema, StringComparison.Ordinal) ||
            packet.ContractVersion != CiWorkflowEvidencePacket.CurrentContractVersion ||
            string.IsNullOrWhiteSpace(acceptedEvidence.PacketSha256) ||
            acceptedEvidence.PacketSha256.Length != 64 ||
            !acceptedEvidence.PacketSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Accepted evidence is not a canonical V1 packet with a valid exact-byte digest.");
        // The acceptance consumer supplies the digest of the exact retrieved bytes. Do not
        // recompute it from the rehydrated object: host trust re-evaluation can normalize
        // the in-memory representation without changing the immutable accepted-byte identity.
        var packetDigest = acceptedEvidence.PacketSha256.ToLowerInvariant();
        var expectedEventSha = runBinding.EventSha ?? mapping.TrustedEventSha ?? mapping.TrustedWorkflowSha;
        var expectedWorkflowRef = mapping.Repository + "/" + mapping.WorkflowPath + "@" + mapping.TrustedWorkflowRef;
        if (!string.Equals(packet.Provenance.Repository, mapping.Repository, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(packet.Provenance.WorkflowPath, mapping.WorkflowPath, StringComparison.Ordinal) ||
            !string.Equals(packet.Provenance.WorkflowRef, expectedWorkflowRef, StringComparison.Ordinal) ||
            !string.Equals(packet.Provenance.WorkflowSha, mapping.TrustedWorkflowSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(packet.Provenance.EventName, runBinding.Event, StringComparison.Ordinal) ||
            !string.Equals(packet.Provenance.EventRef, runBinding.WorkflowRef, StringComparison.Ordinal) ||
            !string.Equals(packet.Provenance.EventSha, expectedEventSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(packet.CandidateRef, mapping.CandidateRef, StringComparison.Ordinal) ||
            !string.Equals(packet.CandidateSha, mapping.CandidateSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(packet.ObservedRun.Repository, mapping.Repository, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(packet.ObservedRun.WorkflowPath, mapping.WorkflowPath, StringComparison.Ordinal) ||
            !string.Equals(packet.ObservedRun.WorkflowRef, mapping.TrustedWorkflowRef, StringComparison.Ordinal) ||
            !string.Equals(packet.ObservedRun.WorkflowSha, expectedEventSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(packet.ObservedRun.Event, runBinding.Event, StringComparison.Ordinal) ||
            packet.ObservedRun.RunId != runBinding.RunId ||
            packet.ObservedRun.RunAttempt != runBinding.RunAttempt)
            throw new UnauthorizedAccessException("Accepted CiAi V1 evidence does not match the persisted workflow, event, candidate, or exact run binding.");

        if (mapping.JobKey is null ||
            packet.RequiredChecks.Count != 1 ||
            !string.Equals(packet.RequiredChecks[0].Key, mapping.JobKey, StringComparison.Ordinal) ||
            !string.Equals(packet.RequiredChecks[0].JobName, mapping.JobName, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Accepted CiAi V1 evidence does not contain the exact host-registered required-check set.");

        var taskStartedAt = task.Events.Count > 0
            ? task.Events[0].OccurredAt
            : throw new InvalidOperationException("Task history has no durable start event.");
        var validationStartedAt = task.Events.LastOrDefault(item => item.Current == WorkTaskStatus.Validating)?.OccurredAt
            ?? throw new InvalidOperationException("Task history has no validation-start event for this attempt.");
        var observedAt = packet.ObservedRun.ObservedAt;
        if (observedAt < validationStartedAt || observedAt > now)
            throw new InvalidDataException("Provider evidence observation is outside the current validation window.");
        if (task.Attempt <= 0)
            throw new InvalidOperationException("Execution has not started a validation attempt for this task.");

        const string branchPrefix = "refs/heads/";
        if (!mapping.CandidateRef.StartsWith(branchPrefix, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Candidate ref is not a branch ref.");
        var candidateBranch = mapping.CandidateRef[branchPrefix.Length..];

        // These decision fields come only from the trusted CiAi V1 packet. No test-results
        // report, model output, serialized notice boolean, or caller-supplied verdict is used.
        var attempt = new WorkTaskRepairAttempt(
            WorkTaskRepairAttempt.CurrentSchemaVersion,
            taskId,
            task.Request.CorrelationId,
            task.Request.Repository,
            candidateBranch,
            mapping.WorkflowPath,
            taskStartedAt,
            task.Attempt,
            packet.CandidateSha,
            packet.ObservedRun.RunId,
            packet.ObservedRun.RunAttempt,
            packetDigest,
            packet.EvidenceComplete,
            packet.IsSuccessful,
            packet.HasActionableDiagnostics,
            packet.DiagnosticsFingerprint,
            validationStartedAt,
            observedAt)
        {
            EvidenceSchema = CiWorkflowEvidencePacket.CurrentSchema
        }.Validate();

        var append = _attemptJournal.Append(attempt);
        if (append is not (WorkTaskRepairAttemptAppendOutcome.Appended or WorkTaskRepairAttemptAppendOutcome.Duplicate))
            throw new InvalidDataException("Authoritative CI attempt could not be appended in order to the durable task evidence journal.");

        var expectation = new WorkTaskRepairEvidenceExpectation(
            taskId,
            task.Request.CorrelationId,
            task.Request.Repository,
            candidateBranch,
            mapping.WorkflowPath,
            mapping.CandidateSha,
            taskStartedAt,
            runBinding.RunId,
            runBinding.RunAttempt);

        return _tasks.ApplyRepairDecision(
            taskId, leaseId, attempt, expectation, maximumDuration, now);
    }
}
