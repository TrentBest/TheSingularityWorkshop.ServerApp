using System.Collections.ObjectModel;
using ServerApp.Core;

namespace ServerApp.Simulation;

/// <summary>Immutable state of one benign virtual agent after a swarm run.</summary>
public sealed record SwarmAgentSnapshot(
    string AgentId,
    string GroupId,
    string AssignedTask,
    int InitialResourceUnits,
    int RemainingResourceUnits,
    bool Acknowledged);

/// <summary>Immutable state of one bounded sub-swarm after a run.</summary>
public sealed record SwarmGroupSnapshot(
    string GroupId,
    IReadOnlyList<string> AgentIds,
    int AssignedTaskCount,
    int AcknowledgedTaskCount);

/// <summary>Reproducible outcome of a process-local virtual swarm coordination run.</summary>
public sealed record AutonomousSwarmResult(
    string RunId,
    int VirtualSteps,
    IReadOnlyDictionary<string, SwarmGroupSnapshot> Groups,
    IReadOnlyDictionary<string, SwarmAgentSnapshot> Agents,
    IReadOnlyList<SimulationMessageTrace> Trace,
    int AssignedTasks,
    int AcknowledgedTasks,
    int TotalInitialResourceUnits,
    int TotalRemainingResourceUnits);

/// <summary>
/// Deterministic simulation-only proof of bounded hierarchical coordination.
/// Agents perform harmless virtual survey tasks; no device, network, actuator, or
/// external command interface exists in this scenario.
/// </summary>
public static class AutonomousSwarmSimulation
{
    public const string RunId = "benign-survey-swarm-v1";
    public const int InitialAgentResourceUnits = 3;
    public const int MaximumGroups = 8;
    public const int MaximumAgentsPerGroup = 8;

    private const string AssignGroupType = "swarm.assign-group";
    private const string AssignTaskType = "swarm.assign-task";
    private const string AcknowledgeType = "swarm.acknowledge";

    /// <summary>
    /// Partitions a bounded set of virtual agents into sub-swarms, assigns one harmless
    /// survey task per agent, and records acknowledgements through the shared message fabric.
    /// </summary>
    public static async ValueTask<AutonomousSwarmResult> RunAsync(
        int groupCount = 3,
        int agentsPerGroup = 4,
        CancellationToken cancellationToken = default)
    {
        if (groupCount is < 1 or > MaximumGroups)
            throw new ArgumentOutOfRangeException(nameof(groupCount));
        if (agentsPerGroup is < 1 or > MaximumAgentsPerGroup)
            throw new ArgumentOutOfRangeException(nameof(agentsPerGroup));
        cancellationToken.ThrowIfCancellationRequested();

        var fabric = new SimulationMessageFabric(RunId);
        var coordinator = new SwarmCoordinator();
        fabric.Register(coordinator);

        var groups = new Dictionary<string, SwarmGroupParticipant>(StringComparer.Ordinal);
        var agents = new Dictionary<string, SwarmAgentParticipant>(StringComparer.Ordinal);
        var nextMessageId = 1;

        for (var groupIndex = 0; groupIndex < groupCount; groupIndex++)
        {
            var groupId = $"group-{groupIndex + 1:D2}";
            var agentIds = Enumerable.Range(0, agentsPerGroup)
                .Select(agentIndex => $"{groupId}-agent-{agentIndex + 1:D2}")
                .ToArray();
            var group = new SwarmGroupParticipant(groupId, agentIds, fabric, () => NextId(ref nextMessageId));
            groups.Add(groupId, group);
            fabric.Register(group);

            foreach (var agentId in agentIds)
            {
                var agent = new SwarmAgentParticipant(
                    agentId, groupId, coordinator.ParticipantId, fabric,
                    () => NextId(ref nextMessageId));
                agents.Add(agentId, agent);
                fabric.Register(agent);
            }
        }

        foreach (var group in groups.Values)
        {
            var message = new SimulationMessageEnvelope(
                NextId(ref nextMessageId), RunId, coordinator.ParticipantId, group.ParticipantId,
                AssignGroupType, 1, 1, 4, group.ParticipantId);
            RequireAccepted(fabric.Enqueue(message), "sub-swarm assignment");
        }

        await fabric.AdvanceToAsync(1, cancellationToken).ConfigureAwait(false);
        await fabric.AdvanceToAsync(2, cancellationToken).ConfigureAwait(false);
        await fabric.AdvanceToAsync(3, cancellationToken).ConfigureAwait(false);

        var agentSnapshots = agents.Values.ToDictionary(
            agent => agent.ParticipantId,
            agent => agent.Snapshot,
            StringComparer.Ordinal);
        var groupSnapshots = groups.Values.ToDictionary(
            group => group.ParticipantId,
            group => group.Snapshot(agentSnapshots),
            StringComparer.Ordinal);

        var assigned = agentSnapshots.Values.Count(agent => !string.IsNullOrEmpty(agent.AssignedTask));
        var acknowledged = agentSnapshots.Values.Count(agent => agent.Acknowledged);
        var initialResources = checked(agentSnapshots.Count * InitialAgentResourceUnits);
        var remainingResources = agentSnapshots.Values.Sum(agent => agent.RemainingResourceUnits);

        if (assigned != agents.Count || acknowledged != assigned ||
            coordinator.AcknowledgedAgents.Count != assigned)
            throw new InvalidOperationException("The swarm run did not complete every assigned virtual task and coordinator acknowledgement.");
        if (remainingResources != initialResources - assigned)
            throw new InvalidOperationException("The swarm resource accounting invariant failed.");

        return new AutonomousSwarmResult(
            RunId,
            3,
            new ReadOnlyDictionary<string, SwarmGroupSnapshot>(groupSnapshots),
            new ReadOnlyDictionary<string, SwarmAgentSnapshot>(agentSnapshots),
            fabric.Trace,
            assigned,
            acknowledged,
            initialResources,
            remainingResources);
    }

    private static Guid NextId(ref int sequence)
    {
        var value = sequence++;
        return Guid.Parse($"60000000-0000-0000-0000-{value:D12}");
    }

    private static void RequireAccepted(SimulationMessageTrace trace, string description)
    {
        if (trace.Disposition != SimulationMessageDisposition.Accepted)
            throw new InvalidOperationException($"The fabric rejected a {description}.");
    }

    private sealed class SwarmCoordinator : ISimulationMessageParticipant
    {
        public string ParticipantId => "swarm-coordinator";
        public string RoleId => "benign-swarm-coordinator";
        public IReadOnlyCollection<string> AcceptedMessageTypes { get; } = [AcknowledgeType];
        public HashSet<string> AcknowledgedAgents { get; } = new(StringComparer.Ordinal);

        public ValueTask ReceiveAsync(SimulationMessageEnvelope message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (message.SchemaVersion != 1 || message.MessageType != AcknowledgeType ||
                !AcknowledgedAgents.Add(message.SenderParticipantId))
                throw new InvalidOperationException("The coordinator received an invalid or duplicate acknowledgement.");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SwarmGroupParticipant(
        string groupId,
        IReadOnlyList<string> agentIds,
        SimulationMessageFabric fabric,
        Func<Guid> nextId) : ISimulationMessageParticipant
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
                var task = $"survey-cell:{ParticipantId}:{index + 1:D2}";
                var assignment = new SimulationMessageEnvelope(
                    nextId(), RunId, ParticipantId, agentId, AssignTaskType, 1, 2, 4, task);
                RequireAccepted(fabric.Enqueue(assignment), "agent task assignment");
            }

            return ValueTask.CompletedTask;
        }

        public SwarmGroupSnapshot Snapshot(IReadOnlyDictionary<string, SwarmAgentSnapshot> agents)
        {
            var snapshots = agentIds.Select(id => agents[id]).ToArray();
            return new SwarmGroupSnapshot(
                ParticipantId,
                Array.AsReadOnly(agentIds.ToArray()),
                snapshots.Count(agent => !string.IsNullOrEmpty(agent.AssignedTask)),
                snapshots.Count(agent => agent.Acknowledged));
        }
    }

    private sealed class SwarmAgentParticipant(
        string agentId,
        string groupId,
        string coordinatorId,
        SimulationMessageFabric fabric,
        Func<Guid> nextId) : ISimulationMessageParticipant
    {
        private string _assignedTask = string.Empty;
        private int _remainingResourceUnits = InitialAgentResourceUnits;
        private bool _acknowledged;

        public string ParticipantId { get; } = agentId;
        public string RoleId => "benign-survey-agent";
        public IReadOnlyCollection<string> AcceptedMessageTypes { get; } = [AssignTaskType];

        public SwarmAgentSnapshot Snapshot => new(
            ParticipantId, groupId, _assignedTask,
            InitialAgentResourceUnits, _remainingResourceUnits, _acknowledged);

        public ValueTask ReceiveAsync(SimulationMessageEnvelope message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (message.SchemaVersion != 1 || _assignedTask.Length != 0 ||
                _remainingResourceUnits < 1 ||
                !message.Payload.StartsWith($"survey-cell:{groupId}:", StringComparison.Ordinal))
                throw new InvalidOperationException("The agent received an invalid, duplicate, or unaffordable task.");

            _assignedTask = message.Payload;
            _remainingResourceUnits--;
            var acknowledgement = new SimulationMessageEnvelope(
                nextId(), RunId, ParticipantId, coordinatorId,
                AcknowledgeType, 1, 3, 5, _assignedTask);
            RequireAccepted(fabric.Enqueue(acknowledgement), "agent acknowledgement");
            _acknowledged = true;
            return ValueTask.CompletedTask;
        }
    }
}
