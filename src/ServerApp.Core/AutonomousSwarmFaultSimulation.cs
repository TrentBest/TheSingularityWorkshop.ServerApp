using System.Collections.ObjectModel;
using ServerApp.Core;

namespace ServerApp.Simulation;

/// <summary>Terminal state of one agent in a fault-injected hierarchical swarm run.</summary>
public enum SwarmAgentExecutionState
{
    Completed,
    Failed,
    Expired,
    Unconfirmed
}

/// <summary>Immutable per-agent evidence from a full hierarchical swarm run.</summary>
public sealed record SwarmAgentExecutionSnapshot(
    string AgentId,
    string GroupId,
    string AssignedTask,
    SwarmAgentExecutionState State,
    int InitialResourceUnits,
    int RemainingResourceUnits,
    bool CoordinatorAcknowledged);

/// <summary>Aggregate result of a deterministic fault-injected multi-group swarm.</summary>
public sealed record AutonomousSwarmFaultResult(
    SwarmFaultMode FaultMode,
    string FaultedAgentId,
    SwarmFaultOutcome Outcome,
    IReadOnlyDictionary<string, SwarmAgentExecutionSnapshot> Agents,
    IReadOnlyList<SimulationMessageTrace> Trace,
    int AssignedTasks,
    int AcknowledgedTasks,
    int TotalInitialResourceUnits,
    int TotalRemainingResourceUnits);

/// <summary>
/// Runs the complete bounded 3-group/12-agent hierarchy with one deterministic injected fault.
/// The host still registers every participant and controls all virtual delivery steps.
/// </summary>
public static class AutonomousSwarmFaultSimulation
{
    private const string RunId = "benign-survey-swarm-fault-v1";
    private const string AssignGroupType = "swarm.assign-group";
    private const string AssignTaskType = "swarm.assign-task";
    private const string AcknowledgeType = "swarm.acknowledge";

    public static async ValueTask<AutonomousSwarmFaultResult> RunAsync(
        SwarmFaultMode faultMode,
        string faultedAgentId = "group-02-agent-02",
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(faultMode))
            throw new ArgumentOutOfRangeException(nameof(faultMode));
        ArgumentException.ThrowIfNullOrWhiteSpace(faultedAgentId);
        var validTargets = Enumerable.Range(1, 3)
            .SelectMany(group => Enumerable.Range(1, 4)
                .Select(agent => $"group-{group:D2}-agent-{agent:D2}"));
        if (!validTargets.Contains(faultedAgentId, StringComparer.Ordinal))
            throw new ArgumentException("The fault target must identify one of the twelve registered agents.", nameof(faultedAgentId));
        cancellationToken.ThrowIfCancellationRequested();

        var fabric = new SimulationMessageFabric(RunId);
        var ledger = new SwarmExecutionLedger();
        var coordinator = new Coordinator(ledger);
        fabric.Register(coordinator);
        var agents = new Dictionary<string, Agent>(StringComparer.Ordinal);
        var groups = new List<Group>();
        var messageSequence = 1;

        Guid NextId() => Guid.Parse($"62000000-0000-0000-0000-{messageSequence++:D12}");

        for (var groupIndex = 1; groupIndex <= 3; groupIndex++)
        {
            var groupId = $"group-{groupIndex:D2}";
            var agentIds = Enumerable.Range(1, 4)
                .Select(index => $"{groupId}-agent-{index:D2}").ToArray();
            var group = new Group(groupId, agentIds, fabric, ledger, NextId, faultMode, faultedAgentId);
            groups.Add(group);
            fabric.Register(group);
            foreach (var agentId in agentIds)
            {
                var taskName = $"survey-cell:{groupId}:{agentId[^2..]}";
                var agent = new Agent(agentId, groupId, taskName, coordinator.ParticipantId, fabric,
                    ledger, NextId, faultMode, faultedAgentId);
                agents.Add(agentId, agent);
                fabric.Register(agent);
            }
        }

        foreach (var group in groups)
        {
            var assignment = new SimulationMessageEnvelope(
                NextId(), RunId, coordinator.ParticipantId, group.ParticipantId,
                AssignGroupType, 1, 1, 4, group.ParticipantId);
            RequireAccepted(fabric.Enqueue(assignment), "group assignment");
        }

        await fabric.AdvanceToAsync(1, cancellationToken).ConfigureAwait(false);
        await fabric.AdvanceToAsync(2, cancellationToken).ConfigureAwait(false);
        await fabric.AdvanceToAsync(4, cancellationToken).ConfigureAwait(false);

        var trace = fabric.Trace;
        if (faultMode == SwarmFaultMode.DelayedTaskExpiry &&
            trace.Any(item => item.Disposition == SimulationMessageDisposition.Expired))
            ledger.MarkExpired(faultedAgentId);
        var snapshots = agents.Values.ToDictionary(
            agent => agent.ParticipantId,
            agent => agent.Snapshot(ledger, coordinator.AcknowledgedAgents.Contains(agent.ParticipantId)),
            StringComparer.Ordinal);

        // Assignment count is the number of host-issued task envelopes, not acknowledgements.
        var assigned = agents.Count;
        var acknowledged = coordinator.AcknowledgedAgents.Count;
        var initialResources = agents.Count * AutonomousSwarmSimulation.InitialAgentResourceUnits;
        var remainingResources = snapshots.Values.Sum(agent => agent.RemainingResourceUnits);

        if (ledger.IssuedCount != assigned || ledger.AcknowledgedCount != acknowledged ||
            acknowledged > assigned || remainingResources < 0 ||
            remainingResources > initialResources)
            throw new InvalidOperationException("The fault-injected swarm violated its aggregate accounting bounds.");

        var outcome = acknowledged == assigned
            ? SwarmFaultOutcome.Completed
            : snapshots.Values.Any(agent => agent.State == SwarmAgentExecutionState.Failed)
                ? SwarmFaultOutcome.Failed
                : SwarmFaultOutcome.Incomplete;
        if ((outcome == SwarmFaultOutcome.Completed) != (acknowledged == assigned))
            throw new InvalidOperationException("The coordinator completion decision disagrees with acknowledgement evidence.");
        if (outcome != SwarmFaultOutcome.Completed && acknowledged != assigned - 1)
            throw new InvalidOperationException("The injected single-agent fault did not produce exactly one missing acknowledgement.");

        return new AutonomousSwarmFaultResult(
            faultMode, faultedAgentId, outcome,
            new ReadOnlyDictionary<string, SwarmAgentExecutionSnapshot>(snapshots),
            trace, assigned, acknowledged, initialResources, remainingResources);
    }

    private static void RequireAccepted(SimulationMessageTrace trace, string description)
    {
        if (trace.Disposition != SimulationMessageDisposition.Accepted)
            throw new InvalidOperationException($"The fabric rejected a {description}.");
    }

    private sealed class Coordinator(SwarmExecutionLedger ledger) : ISimulationMessageParticipant
    {
        public string ParticipantId => "swarm-fault-coordinator";
        public string RoleId => "benign-swarm-coordinator";
        public IReadOnlyCollection<string> AcceptedMessageTypes { get; } = [AcknowledgeType];
        public HashSet<string> AcknowledgedAgents { get; } = new(StringComparer.Ordinal);

        public ValueTask ReceiveAsync(SimulationMessageEnvelope message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (message.SchemaVersion != 1 || message.MessageType != AcknowledgeType ||
                !ledger.TryAcknowledge(message.SenderParticipantId, message.Payload) ||
                !AcknowledgedAgents.Add(message.SenderParticipantId))
                throw new InvalidOperationException("The coordinator received an invalid or duplicate acknowledgement.");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Group(
        string groupId,
        IReadOnlyList<string> agentIds,
        SimulationMessageFabric fabric,
        SwarmExecutionLedger ledger,
        Func<Guid> nextId,
        SwarmFaultMode faultMode,
        string faultedAgentId) : ISimulationMessageParticipant
    {
        public string ParticipantId { get; } = groupId;
        public string RoleId => "benign-survey-subswarm";
        public IReadOnlyCollection<string> AcceptedMessageTypes { get; } = [AssignGroupType];

        public ValueTask ReceiveAsync(SimulationMessageEnvelope message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (message.SchemaVersion != 1 || message.Payload != ParticipantId)
                throw new InvalidOperationException("The sub-swarm received an invalid assignment.");

            for (var index = 0; index < agentIds.Count; index++)
            {
                var agentId = agentIds[index];
                var delayed = faultMode == SwarmFaultMode.DelayedTaskExpiry && agentId == faultedAgentId;
                var task = $"survey-cell:{ParticipantId}:{index + 1:D2}";
                ledger.Issue(agentId, task);
                var assignment = new SimulationMessageEnvelope(
                    nextId(), RunId, ParticipantId, agentId, AssignTaskType, 1,
                    delayed ? 3 : 2, delayed ? 4 : 5, task);
                RequireAccepted(fabric.Enqueue(assignment), "agent task assignment");
                if (faultMode == SwarmFaultMode.DuplicateTaskAttempt && agentId == faultedAgentId)
                {
                    var duplicate = fabric.Enqueue(assignment);
                    if (duplicate.Disposition != SimulationMessageDisposition.Duplicate)
                        throw new InvalidOperationException("The duplicate task attempt was not suppressed.");
                }
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Agent(
        string agentId,
        string groupId,
        string taskName,
        string coordinatorId,
        SimulationMessageFabric fabric,
        SwarmExecutionLedger ledger,
        Func<Guid> nextId,
        SwarmFaultMode faultMode,
        string faultedAgentId) : ISimulationMessageParticipant
    {
        private string _assignedTask = string.Empty;
        private int _remaining = AutonomousSwarmSimulation.InitialAgentResourceUnits;
        private bool _failed;

        public string ParticipantId { get; } = agentId;
        public string RoleId => "benign-survey-agent";
        public IReadOnlyCollection<string> AcceptedMessageTypes { get; } = [AssignTaskType];

        public ValueTask ReceiveAsync(SimulationMessageEnvelope message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (message.SchemaVersion != 1 || _assignedTask.Length != 0 ||
                !message.Payload.StartsWith($"survey-cell:{groupId}:", StringComparison.Ordinal))
                throw new InvalidOperationException("The agent received an invalid or duplicate task.");

            if (faultMode == SwarmFaultMode.ParticipantHandlerFailure && ParticipantId == faultedAgentId)
            {
                ledger.MarkFailed(ParticipantId, message.Payload);
                _failed = true;
                throw new InvalidOperationException("Injected participant handler failure.");
            }

            ledger.MarkDelivered(ParticipantId, message.Payload);
            _assignedTask = message.Payload;
            _remaining--;
            var acknowledgement = new SimulationMessageEnvelope(
                nextId(), RunId, ParticipantId, coordinatorId, AcknowledgeType,
                1, 3, 6, _assignedTask);
            RequireAccepted(fabric.Enqueue(acknowledgement), "agent acknowledgement");
            return ValueTask.CompletedTask;
        }

        public SwarmAgentExecutionSnapshot Snapshot(
            SwarmExecutionLedger ledger,
            bool acknowledged)
        {
            var state = ledger.GetState(ParticipantId) switch
            {
                SwarmTaskLifecycle.Completed => SwarmAgentExecutionState.Completed,
                SwarmTaskLifecycle.Failed => SwarmAgentExecutionState.Failed,
                SwarmTaskLifecycle.Expired => SwarmAgentExecutionState.Expired,
                _ => SwarmAgentExecutionState.Unconfirmed
            };
            return new SwarmAgentExecutionSnapshot(
                ParticipantId, groupId, ledger.GetTaskId(ParticipantId),
                state, AutonomousSwarmSimulation.InitialAgentResourceUnits, _remaining, acknowledged);
        }
    }
}
