using System.Security.Cryptography;
using System.Text;
using TheSingularityWorkshop.Execution;

namespace ServerApp.ExecutionHost;

/// <summary>
/// Composes ServerApp's durable task host, configured HeadlessAi turn coordinator,
/// host-owned operation policy/executor, and durable human approval queue into one
/// bounded autonomous-development entry point.
/// </summary>
public sealed class ServerAppAutonomousDevelopmentHost
{
    public ServerAppAutonomousDevelopmentHost(
        PersistentWorkTaskCoordinator tasks,
        ExecutionHeadlessAiTurnCoordinator headlessAiTurns,
        WorkTaskCapabilityRegistry capabilities,
        IExecutionAgentOperationPolicy operationPolicy,
        IExecutionAgentOperationExecutor operationExecutor,
        HumanApprovalQueueHost approvals)
    {
        ArgumentNullException.ThrowIfNull(approvals);
        var router = new ExecutionAgentOperationRouter(
            capabilities, operationPolicy, operationExecutor, approvals.Store);
        AgentTurns = new ExecutionAgentTurnOrchestrator(tasks, headlessAiTurns, router);
    }

    /// <summary>
    /// Invokes one configured LLM turn for a leased task, then routes its structured
    /// operation proposals through host policy, capabilities, and the approval queue.
    /// </summary>
    public ExecutionAgentTurnOrchestrator AgentTurns { get; }

    /// <summary>
    /// Packages a host-verified CI report into one bounded HeadlessAi turn, then routes
    /// the model's structured operation proposals through policy, capability, or approval.
    /// The caller must authenticate the report notice and independently verify its artifact
    /// provenance before invoking this method.
    /// </summary>
    public Task<ExecutionAgentTurnOutcome> ProcessVerifiedBuildReportAsync(
        string taskId, Guid leaseId, string authorizedBranch, string candidateCommitSha,
        Guid capabilityId, string reportIdentity, string reportSha256, string verifiedReportJson,
        IReadOnlyList<ExecutionHeadlessAiEvidence> evidence,
        string? priorTurnId = null, string? priorCandidateCommitSha = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedReportJson);
        ArgumentNullException.ThrowIfNull(evidence);
        if (reportIdentity.Length > 256 || reportIdentity.Any(char.IsControl) ||
            reportSha256.Length != 64 || !reportSha256.All(Uri.IsHexDigit) ||
            verifiedReportJson.Length > 48 * 1024)
            throw new ArgumentException("Verified report identity, digest, or payload exceeds the supported bounds.");
        var actualDigest = SHA256.HashData(Encoding.UTF8.GetBytes(verifiedReportJson));
        var expectedDigest = Convert.FromHexString(reportSha256);
        if (!CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest))
            throw new UnauthorizedAccessException("Verified report payload does not match its accepted SHA-256 digest.");

        var instructions = "Analyze the host-verified CI report and propose the next bounded repository operation. " +
            "Return only the structured response schema. Use only the listed operation vocabulary. " +
            "Do not claim that a proposed operation was executed. If no safe operation is justified, return no operations and explain the blocker.";
        var canonicalPrompt = "ADP verified build report\n" +
            "Report identity: " + reportIdentity + "\n" +
            "Report SHA-256: " + reportSha256.ToLowerInvariant() + "\n" +
            "Exact verified report JSON:\n" + verifiedReportJson;
        if (canonicalPrompt.Length > 64_000)
            throw new ArgumentException("The packaged build report exceeds the HeadlessAi prompt bound.");

        return AgentTurns.InvokeAndRouteAsync(taskId, leaseId, authorizedBranch,
            candidateCommitSha, capabilityId, "implementation-agent", instructions,
            canonicalPrompt, ExecutionHeadlessAiResponseSchema.JsonSchema, evidence,
            priorTurnId, priorCandidateCommitSha, approvalLifetime: TimeSpan.FromDays(1),
            cancellationToken: cancellationToken);
    }
}
