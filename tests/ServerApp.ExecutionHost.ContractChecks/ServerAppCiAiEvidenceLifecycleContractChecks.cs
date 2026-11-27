using System.Security.Cryptography;
using System.Text;
using ServerApp.ExecutionHost;
using TheSingularityWorkshop.CiAi;
using TheSingularityWorkshop.Execution;

internal static class ServerAppCiAiEvidenceLifecycleContractChecks
{
    private const string Repository = "owner/repo";
    private const string WorkflowPath = ".github/workflows/ci.yml";
    private const string JobKey = "fsm-api-proof";
    private const string JobName = "Build, execute, and capture state evidence";
    private const string WorkflowSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string EventSha = "dddddddddddddddddddddddddddddddddddddddd";
    private const string CandidateSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string CandidateRef = "refs/heads/feature/task-1";
    private const long RunId = 42;
    private const int RunAttempt = 1;
    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.Parse("2026-11-27T10:00:00Z");
    private static readonly DateTimeOffset ValidationStartedAt = CreatedAt.AddMinutes(10);
    private static readonly DateTimeOffset ObservedAt = CreatedAt.AddMinutes(40);
    private static readonly DateTimeOffset DecisionAt = CreatedAt.AddMinutes(41);

    public static void Run()
    {
        TrustedActionableFailureRequiresRepair();
        TrustedSuccessBecomesReviewReady();
        UntrustedFailureCannotAuthorizeRepair();
        MismatchedCandidateCannotChangeTaskState();
    }

    private static void TrustedActionableFailureRequiresRepair()
    {
        var fixture = CreateTaskFixture("task-failure");
        try
        {
            var request = CreateRequest("task-failure", "correlation-task-failure");
            var accepted = Accept(CreatePacket(successful: false, workflowTrusted: true));
            var lifecycle = new ServerAppCiAiEvidenceLifecycleCoordinator(fixture.Tasks, fixture.Attempts);
            var result = lifecycle.ApplyAuthoritativeEvidence(
                "task-failure", fixture.LeaseId, request, accepted, TimeSpan.FromHours(4), DecisionAt);

            Assert(result.Status == WorkTaskStatus.RepairRequired,
                "trusted actionable V1 failure did not produce RepairRequired");
            var attempt = fixture.Attempts.GetAttempts("task-failure").Single();
            Assert(attempt.EvidenceSchema == CiWorkflowEvidencePacket.CurrentSchema &&
                attempt.EvidencePayloadSha256 == accepted.PacketSha256 &&
                attempt.CandidateSha == CandidateSha &&
                attempt.CiRunId == RunId && attempt.CiRunAttempt == RunAttempt,
                "durable repair attempt lost exact V1 packet, candidate, or run identity");
            Assert(result.Events[^1].EvidenceSchema == CiWorkflowEvidencePacket.CurrentSchema &&
                result.Events[^1].EvidencePayloadSha256 == accepted.PacketSha256,
                "lifecycle transition was not bound to the exact V1 packet digest");
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    private static void TrustedSuccessBecomesReviewReady()
    {
        var fixture = CreateTaskFixture("task-success");
        try
        {
            var request = CreateRequest("task-success", "correlation-task-success");
            var accepted = Accept(CreatePacket(successful: true, workflowTrusted: true));
            var lifecycle = new ServerAppCiAiEvidenceLifecycleCoordinator(fixture.Tasks, fixture.Attempts);
            var result = lifecycle.ApplyAuthoritativeEvidence(
                "task-success", fixture.LeaseId, request, accepted, TimeSpan.FromHours(4), DecisionAt);

            Assert(result.Status == WorkTaskStatus.ReviewReady,
                "trusted complete successful V1 evidence did not produce ReviewReady");
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    private static void UntrustedFailureCannotAuthorizeRepair()
    {
        var fixture = CreateTaskFixture("task-untrusted");
        try
        {
            var request = CreateRequest("task-untrusted", "correlation-task-untrusted");
            var accepted = Accept(CreatePacket(successful: false, workflowTrusted: false));
            var lifecycle = new ServerAppCiAiEvidenceLifecycleCoordinator(fixture.Tasks, fixture.Attempts);
            var result = lifecycle.ApplyAuthoritativeEvidence(
                "task-untrusted", fixture.LeaseId, request, accepted, TimeSpan.FromHours(4), DecisionAt);

            Assert(result.Status == WorkTaskStatus.CodeRed,
                "untrusted workflow failure was allowed to authorize an autonomous repair");
            Assert(!fixture.Attempts.GetAttempts("task-untrusted").Single().HasActionableDiagnostics,
                "untrusted packet was incorrectly persisted as actionable diagnostics");
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    private static void MismatchedCandidateCannotChangeTaskState()
    {
        var fixture = CreateTaskFixture("task-mismatch");
        try
        {
            var request = CreateRequest("task-mismatch", "correlation-task-mismatch",
                candidateSha: "cccccccccccccccccccccccccccccccccccccccc");
            var accepted = Accept(CreatePacket(successful: false, workflowTrusted: true));
            var lifecycle = new ServerAppCiAiEvidenceLifecycleCoordinator(fixture.Tasks, fixture.Attempts);
            AssertThrows<UnauthorizedAccessException>(() => lifecycle.ApplyAuthoritativeEvidence(
                "task-mismatch", fixture.LeaseId, request, accepted, TimeSpan.FromHours(4), DecisionAt));
            Assert(fixture.Tasks.GetSnapshot("task-mismatch").Status == WorkTaskStatus.Validating &&
                fixture.Attempts.GetAttempts("task-mismatch").Count == 0,
                "mismatched candidate evidence created a task transition or repair-attempt record");
        }
        finally
        {
            Directory.Delete(fixture.Root, recursive: true);
        }
    }

    private static ExecutionAcceptedCiAiWorkflowEvidence Accept(CiWorkflowEvidencePacket packet) =>
        new(packet, packet.PayloadSha256);

    private static CiAiRequestTaskMappingSnapshot CreateRequest(
        string taskId,
        string correlationId,
        string candidateSha = CandidateSha)
    {
        var mapping = new CiAiRequestTaskMapping(
            1, "request-" + taskId, "operation-" + taskId, "idempotency-" + taskId,
            taskId, correlationId, Repository, CandidateRef, candidateSha, WorkflowPath,
            "refs/heads/development", WorkflowSha, JobName,
            "Resolve candidate", "Verify candidate checkout", "ciai-production",
            "https://artifacts.example.test/",
            "https://ciai.example.test/api/v1/integrations/ciai/artifact-receipts",
            "ciai-ack-profile", CreatedAt, CreatedAt.AddHours(2), TrustedEventSha: EventSha)
        {
            JobKey = JobKey,
            SourceArtifactName = "ciai-test-results-1"
        }.Validate();
        var binding = new CiAiDispatchRunBinding(
            RunId, RunAttempt, Repository, WorkflowPath, "refs/heads/development",
            WorkflowSha, CandidateRef, candidateSha, "workflow_dispatch",
            CreatedAt.AddMinutes(1), EventSha: EventSha);
        return new CiAiRequestTaskMappingSnapshot(mapping, binding, Cancelled: false);
    }

    private static CiWorkflowEvidencePacket CreatePacket(bool successful, bool workflowTrusted)
    {
        var json = """
        {
          "schema": "__SCHEMA__",
          "repository": "owner/repo",
          "runId": 42,
          "runAttempt": 1,
          "workflow": {
            "ref": "owner/repo/.github/workflows/ci.yml@refs/heads/development",
            "sha": "__WORKFLOW_SHA__"
          },
          "event": {
            "name": "workflow_dispatch",
            "ref": "refs/heads/development",
            "sha": "__EVENT_SHA__",
            "pullRequest": null
          },
          "checkout": {
            "ref": "refs/heads/feature/task-1",
            "sha": "__CANDIDATE_SHA__"
          }
        }
        """;
        json = json.Replace("__SCHEMA__", CiWorkflowProvenanceRecord.CurrentSchema, StringComparison.Ordinal)
            .Replace("__WORKFLOW_SHA__", WorkflowSha, StringComparison.Ordinal)
            .Replace("__EVENT_SHA__", EventSha, StringComparison.Ordinal)
            .Replace("__CANDIDATE_SHA__", CandidateSha, StringComparison.Ordinal);
        var provenanceBytes = Encoding.UTF8.GetBytes(json);
        var provenance = CiWorkflowProvenanceRecord.ParseAndValidate(
            provenanceBytes, Repository, RunId, RunAttempt,
            Convert.ToHexString(SHA256.HashData(provenanceBytes)));

        var outcome = successful ? "success" : "failure";
        var steps = Array.AsReadOnly(new[]
        {
            new CiWorkflowStepObservation(1, "Resolve candidate", "completed", "success", ObservedAt.ToString("O")),
            new CiWorkflowStepObservation(2, "Verify candidate checkout", "completed", "success", ObservedAt.ToString("O"))
        });
        var job = new CiWorkflowJobObservation(
            100, RunId, RunAttempt, JobName, "completed", outcome,
            new Uri($"https://github.com/{Repository}/actions/runs/{RunId}/job/100"), steps)
        {
            StartedAt = ValidationStartedAt,
            CompletedAt = ObservedAt.AddMinutes(-1)
        };
        var checkout = new CiWorkflowCandidateCheckoutEvidence(
            CandidateRef, CandidateSha, JobName, "Resolve candidate", "Verify candidate checkout");
        var observed = new CiWorkflowRunEvidenceObservation(
            Repository, WorkflowPath, RunId, RunAttempt,
            "refs/heads/development", EventSha, "workflow_dispatch", "CI",
            CiRunStatus.Completed,
            successful ? CiRunConclusion.Success : CiRunConclusion.Failure,
            new Uri($"https://github.com/{Repository}/actions/runs/{RunId}"),
            ObservedAt, Array.AsReadOnly(new[] { job }), true, true,
            candidateCheckoutEvidence: checkout);
        return CiWorkflowEvidencePacket.FromValidatedProvenance(
            observed, provenance, workflowDefinitionTrusted: workflowTrusted,
            new[] { new CiWorkflowRequiredCheckBinding(JobKey, JobName) });
    }

    private static TaskFixture CreateTaskFixture(string taskId)
    {
        var root = Path.Combine(Path.GetTempPath(), "serverapp-ciai-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var attempts = new JsonlWorkTaskRepairAttemptJournal(Path.Combine(root, "attempts.jsonl"));
        var tasks = new PersistentWorkTaskCoordinator(
            new JsonlWorkTaskStore(Path.Combine(root, "tasks.jsonl")), attempts);
        tasks.Submit(new WorkTaskRequest(
            1, taskId, "correlation-" + taskId, Repository, 10,
            "https://github.com/owner/repo/issues/10",
            "cccccccccccccccccccccccccccccccccccccccc",
            new[] { "src" }, 3));
        var claimed = tasks.Claim(taskId, "agent-1", TimeSpan.FromHours(1), CreatedAt);
        var leaseId = claimed.Lease!.LeaseId;
        tasks.Transition(taskId, leaseId, WorkTaskStatus.Investigating, CreatedAt.AddMinutes(2), "Investigate task.");
        tasks.Transition(taskId, leaseId, WorkTaskStatus.Implementing, CreatedAt.AddMinutes(5), "Candidate prepared.");
        tasks.Transition(taskId, leaseId, WorkTaskStatus.Validating, ValidationStartedAt, "Validate exact candidate.");
        return new TaskFixture(root, tasks, attempts, leaseId);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void AssertThrows<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException("Expected " + typeof(TException).Name + ".");
    }

    private sealed record TaskFixture(
        string Root,
        PersistentWorkTaskCoordinator Tasks,
        JsonlWorkTaskRepairAttemptJournal Attempts,
        Guid LeaseId);
}
