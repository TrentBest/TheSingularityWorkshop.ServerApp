using ServerApp.Core;

namespace ServerApp.Simulation;

/// <summary>Deterministic fault applied to one benign virtual swarm task.</summary>
public enum SwarmFaultMode
{
    ParticipantHandlerFailure,
    DelayedTaskExpiry,
    DuplicateTaskAttempt
}

/// <summary>Inspectable terminal state of a fault-injection run.</summary>
public enum SwarmFaultOutcome
{
    Completed,
    Incomplete,
    Failed
}

/// <summary>Immutable evidence from one bounded swarm fault-injection run.</summary>
public sealed record SwarmFaultSimulationResult(
    SwarmFaultMode FaultMode,
    SwarmFaultOutcome Outcome,
    int AcceptedTaskMessages,
    int DuplicateTaskMessages,
    int DeliveredTaskMessages,
    int ExpiredTaskMessages,
    int FailedTaskMessages,
    int CoordinatorAcknowledgements,
    IReadOnlyList<SimulationMessageTrace> Trace);

/// <summary>
/// Exercises failure and duplicate semantics using only the process-local simulation fabric.
/// This harness has no physical, network, or external side effects and never retries automatically.
/// </summary>
public static class SwarmFaultInjectionSimulation
{
    private const string RunId = "benign-swarm-fault-proof-v1";
    private const string TaskType = "swarm.fault.task";
    private const string AcknowledgeType = "swarm.fault.acknowledge";

    public static async ValueTask<SwarmFaultSimulationResult> RunAsync(
        SwarmFaultMode faultMode,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(faultMode))
            throw new ArgumentOutOfRangeException(nameof(faultMode));
        cancellationToken.ThrowIfCancellationRequested();

        var fabric = new SimulationMessageFabric(RunId);
        var coordinator = new FaultScenarioCoordinator();
        var agent = new FaultScenarioAgent(fabric, coordinator.ParticipantId, faultMode);
        fabric.Register(coordinator);
        fabric.Register(agent);

        var delayed = faultMode == SwarmFaultMode.DelayedTaskExpiry;
        var task = new SimulationMessageEnvelope(
            Guid.Parse("61000000-0000-0000-0000-000000000001"),
            RunId,
            coordinator.ParticipantId,
            agent.ParticipantId,
            TaskType,
            1,
            delayed ? 2 : 1,
            delayed ? 3 : 4,
            "survey-cell:bounded-fault-proof");

        var first = fabric.Enqueue(task);
        if (first.Disposition != SimulationMessageDisposition.Accepted)
            throw new InvalidOperationException("The host could not enqueue the initial fault-proof task.");

        if (faultMode == SwarmFaultMode.DuplicateTaskAttempt)
        {
            var duplicate = fabric.Enqueue(task);
            if (duplicate.Disposition != SimulationMessageDisposition.Duplicate)
                throw new InvalidOperationException("The fabric did not suppress an identical duplicate task attempt.");
        }

        await fabric.AdvanceToAsync(delayed ? 3 : 1, cancellationToken).ConfigureAwait(false);

        var trace = fabric.Trace;
        var taskObservations = trace.Where(item => item.MessageId == task.MessageId).ToArray();
        var outcome = coordinator.Acknowledgements == 1
            ? SwarmFaultOutcome.Completed
            : taskObservations.Any(item => item.Disposition == SimulationMessageDisposition.Failed)
                ? SwarmFaultOutcome.Failed
                : SwarmFaultOutcome.Incomplete;

        return new SwarmFaultSimulationResult(
            faultMode,
            outcome,
            taskObservations.Count(item => item.Disposition == SimulationMessageDisposition.Accepted),
            taskObservations.Count(item => item.Disposition == SimulationMessageDisposition.Duplicate),
            taskObservations.Count(item => item.Disposition == SimulationMessageDisposition.Delivered),
            taskObservations.Count(item => item.Disposition == SimulationMessageDisposition.Expired),
            taskObservations.Count(item => item.Disposition == SimulationMessageDisposition.Failed),
            coordinator.Acknowledgements,
            trace);
    }

    private sealed class FaultScenarioCoordinator : ISimulationMessageParticipant
    {
        public string ParticipantId => "fault-proof-coordinator";
        public string RoleId => "benign-fault-proof-coordinator";
        public IReadOnlyCollection<string> AcceptedMessageTypes { get; } = [AcknowledgeType];
        public int Acknowledgements { get; private set; }

        public ValueTask ReceiveAsync(SimulationMessageEnvelope message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (message.SchemaVersion != 1 || message.MessageType != AcknowledgeType ||
                message.Payload != "survey-cell:bounded-fault-proof" || Acknowledgements != 0)
                throw new InvalidOperationException("The coordinator received an invalid or duplicate acknowledgement.");
            Acknowledgements++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FaultScenarioAgent(
        SimulationMessageFabric fabric,
        string coordinatorId,
        SwarmFaultMode faultMode) : ISimulationMessageParticipant
    {
        public string ParticipantId => "fault-proof-agent";
        public string RoleId => "benign-fault-proof-agent";
        public IReadOnlyCollection<string> AcceptedMessageTypes { get; } = [TaskType];

        public ValueTask ReceiveAsync(SimulationMessageEnvelope message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (message.SchemaVersion != 1 || message.MessageType != TaskType ||
                message.Payload != "survey-cell:bounded-fault-proof")
                throw new InvalidOperationException("The agent received an invalid virtual task.");

            if (faultMode == SwarmFaultMode.ParticipantHandlerFailure)
                throw new InvalidOperationException("Injected participant handler failure.");

            var acknowledgement = new SimulationMessageEnvelope(
                Guid.Parse("61000000-0000-0000-0000-000000000002"),
                RunId,
                ParticipantId,
                coordinatorId,
                AcknowledgeType,
                1,
                1,
                4,
                message.Payload);
            var accepted = fabric.Enqueue(acknowledgement);
            if (accepted.Disposition != SimulationMessageDisposition.Accepted)
                throw new InvalidOperationException("The agent acknowledgement was not accepted.");
            return ValueTask.CompletedTask;
        }
    }
}
