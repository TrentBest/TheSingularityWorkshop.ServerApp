namespace ServerApp.Core;

/// <summary>Observable disposition of a message in the in-process simulation fabric.</summary>
public enum SimulationMessageDisposition
{
    Accepted,
    Duplicate,
    Delivered,
    Expired,
    Rejected,
    Failed
}

/// <summary>
/// Immutable, versioned message envelope for one simulation run.
/// Step values are virtual simulation steps, not wall-clock time.
/// </summary>
public sealed record SimulationMessageEnvelope
{
    public SimulationMessageEnvelope(
        Guid messageId,
        string runId,
        string senderParticipantId,
        string recipientParticipantId,
        string messageType,
        int schemaVersion,
        long notBeforeStep,
        long expiresAfterStep,
        string payload)
    {
        if (messageId == Guid.Empty)
            throw new ArgumentException("A message identifier is required.", nameof(messageId));
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(senderParticipantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientParticipantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        if (schemaVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        if (notBeforeStep < 0)
            throw new ArgumentOutOfRangeException(nameof(notBeforeStep));
        if (expiresAfterStep <= notBeforeStep)
            throw new ArgumentOutOfRangeException(nameof(expiresAfterStep),
                "Expiry must be later than the earliest delivery step.");
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length > 65_536)
            throw new ArgumentOutOfRangeException(nameof(payload), "A simulation message payload is limited to 64 KiB.");

        MessageId = messageId;
        RunId = runId.Trim();
        SenderParticipantId = senderParticipantId.Trim();
        RecipientParticipantId = recipientParticipantId.Trim();
        MessageType = messageType.Trim();
        SchemaVersion = schemaVersion;
        NotBeforeStep = notBeforeStep;
        ExpiresAfterStep = expiresAfterStep;
        Payload = payload;
    }

    public Guid MessageId { get; }
    public string RunId { get; }
    public string SenderParticipantId { get; }
    public string RecipientParticipantId { get; }
    public string MessageType { get; }
    public int SchemaVersion { get; }
    public long NotBeforeStep { get; }
    public long ExpiresAfterStep { get; }
    public string Payload { get; }
}

/// <summary>One immutable observation in the simulation message trace.</summary>
public sealed record SimulationMessageTrace(
    Guid MessageId,
    SimulationMessageDisposition Disposition,
    long Step,
    long Sequence,
    string Detail);

/// <summary>
/// A host-approved participant with a finite accepted message vocabulary.
/// Implementations own their state and validate the content of every received message.
/// </summary>
public interface ISimulationMessageParticipant
{
    string ParticipantId { get; }
    string RoleId { get; }
    IReadOnlyCollection<string> AcceptedMessageTypes { get; }

    ValueTask ReceiveAsync(
        SimulationMessageEnvelope message,
        CancellationToken cancellationToken);
}

/// <summary>
/// Deterministic, process-local message fabric for simulation proofs.
/// Delivery is sequential by virtual step and enqueue order. This is not a network broker,
/// durable queue, distributed scheduler, or exactly-once delivery guarantee.
/// </summary>
public sealed class SimulationMessageFabric
{
    private readonly string _runId;
    private readonly Dictionary<string, ParticipantRegistration> _participants =
        new(StringComparer.Ordinal);
    private readonly SortedDictionary<(long Step, long Sequence), SimulationMessageEnvelope> _pending = new();
    private readonly Dictionary<Guid, SimulationMessageEnvelope> _knownMessages = new();
    private readonly List<SimulationMessageTrace> _trace = new();
    private long _nextSequence;
    private long _currentStep;

    public SimulationMessageFabric(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        _runId = runId.Trim();
    }

    public long CurrentStep => _currentStep;

    /// <summary>Returns an immutable snapshot of all fabric observations.</summary>
    public IReadOnlyList<SimulationMessageTrace> Trace =>
        Array.AsReadOnly(_trace.ToArray());

    /// <summary>Registers a trusted participant and snapshots its finite message vocabulary.</summary>
    public void Register(ISimulationMessageParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentException.ThrowIfNullOrWhiteSpace(participant.ParticipantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(participant.RoleId);
        ArgumentNullException.ThrowIfNull(participant.AcceptedMessageTypes);

        var participantId = participant.ParticipantId.Trim();
        var roleId = participant.RoleId.Trim();
        RequireIdentifier(participantId, nameof(participant.ParticipantId));
        RequireIdentifier(roleId, nameof(participant.RoleId));

        var acceptedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaredType in participant.AcceptedMessageTypes)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(declaredType);
            var messageType = declaredType.Trim();
            RequireIdentifier(messageType, nameof(participant.AcceptedMessageTypes));
            if (!acceptedTypes.Add(messageType))
                throw new ArgumentException(
                    $"Participant '{participantId}' declares duplicate message type '{messageType}'.",
                    nameof(participant));
        }

        if (!_participants.TryAdd(participantId,
                new ParticipantRegistration(participant, acceptedTypes)))
            throw new InvalidOperationException(
                $"Simulation participant '{participantId}' is already registered.");
    }

    /// <summary>
    /// Enqueues a message. Repeating an identical message ID is acknowledged as Duplicate
    /// without a second enqueue; reusing that ID for different content is rejected.
    /// </summary>
    public SimulationMessageTrace Enqueue(SimulationMessageEnvelope message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.RunId != _runId)
            return Record(message.MessageId, SimulationMessageDisposition.Rejected,
                _currentStep, 0, "The message belongs to a different simulation run.");

        if (!_participants.ContainsKey(message.SenderParticipantId))
            return Record(message.MessageId, SimulationMessageDisposition.Rejected,
                _currentStep, 0, "The sender is not a registered participant.");

        if (!_participants.ContainsKey(message.RecipientParticipantId))
            return Record(message.MessageId, SimulationMessageDisposition.Rejected,
                _currentStep, 0, "The recipient is not a registered participant.");

        if (message.NotBeforeStep < _currentStep)
            return Record(message.MessageId, SimulationMessageDisposition.Rejected,
                _currentStep, 0, "The requested delivery step is earlier than the current simulation step.");

        if (_knownMessages.TryGetValue(message.MessageId, out var existing))
        {
            return existing == message
                ? Record(message.MessageId, SimulationMessageDisposition.Duplicate,
                    _currentStep, 0, "An identical message with this ID was already accepted.")
                : Record(message.MessageId, SimulationMessageDisposition.Rejected,
                    _currentStep, 0, "The message ID was already used for different content.");
        }

        var sequence = checked(++_nextSequence);
        _knownMessages.Add(message.MessageId, message);
        _pending.Add((message.NotBeforeStep, sequence), message);
        return Record(message.MessageId, SimulationMessageDisposition.Accepted,
            _currentStep, sequence, "Message accepted into the in-memory simulation queue.");
    }

    /// <summary>
    /// Advances virtual time monotonically and delivers due messages sequentially.
    /// A message expires when the requested step is greater than or equal to ExpiresAfterStep.
    /// Handler failures are recorded and isolated; the fabric does not retry automatically.
    /// </summary>
    public async ValueTask<IReadOnlyList<SimulationMessageTrace>> AdvanceToAsync(
        long step,
        CancellationToken cancellationToken = default)
    {
        if (step < _currentStep)
            throw new ArgumentOutOfRangeException(nameof(step), "Simulation time cannot move backwards.");

        _currentStep = step;
        var startIndex = _trace.Count;

        while (_pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = _pending.First();
            if (first.Key.Step > step)
                break;

            _pending.Remove(first.Key);
            var message = first.Value;
            var sequence = first.Key.Sequence;

            if (step >= message.ExpiresAfterStep)
            {
                Record(message.MessageId, SimulationMessageDisposition.Expired,
                    step, sequence, "Message expired before it was delivered.");
                continue;
            }

            var recipient = _participants[message.RecipientParticipantId];
            if (!recipient.AcceptedMessageTypes.Contains(message.MessageType))
            {
                Record(message.MessageId, SimulationMessageDisposition.Rejected,
                    step, sequence, "The recipient does not accept this message type.");
                continue;
            }

            try
            {
                await recipient.Participant.ReceiveAsync(message, cancellationToken).ConfigureAwait(false);
                Record(message.MessageId, SimulationMessageDisposition.Delivered,
                    step, sequence, "Recipient handler completed successfully.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Record(message.MessageId, SimulationMessageDisposition.Failed,
                    step, sequence, "Delivery was cancelled while invoking the recipient.");
                throw;
            }
            catch (Exception exception)
            {
                Record(message.MessageId, SimulationMessageDisposition.Failed,
                    step, sequence, $"Recipient handler failed: {exception.GetType().Name}.");
            }
        }

        return Array.AsReadOnly(_trace.Skip(startIndex).ToArray());
    }

    private SimulationMessageTrace Record(
        Guid messageId,
        SimulationMessageDisposition disposition,
        long step,
        long sequence,
        string detail)
    {
        var observation = new SimulationMessageTrace(messageId, disposition, step, sequence, detail);
        _trace.Add(observation);
        return observation;
    }

    private static void RequireIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 128)
            throw new ArgumentOutOfRangeException(parameterName, "Identifiers are limited to 128 characters.");
    }

    private sealed record ParticipantRegistration(
        ISimulationMessageParticipant Participant,
        IReadOnlySet<string> AcceptedMessageTypes);
}
