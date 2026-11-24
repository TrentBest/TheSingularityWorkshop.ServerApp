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
}
