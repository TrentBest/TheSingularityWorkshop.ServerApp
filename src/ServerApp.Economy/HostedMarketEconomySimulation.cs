using System.Collections.ObjectModel;
using System.Text.Json;
using ServerApp.Core;
using TheSingularityWorkshop.Economy;

namespace ServerApp.Economy;

/// <summary>Version-one wire projection of an authoritative Economy ledger event.</summary>
public sealed record MarketEconomyEventMessage(
    Guid OperationId,
    string EventKind,
    decimal Amount,
    Guid CurrencyId,
    string CurrencySymbol,
    DateTimeOffset OccurredAt,
    string LedgerOperationKind,
    IReadOnlyList<MarketEconomyPostingMessage> Postings);

/// <summary>A signed ledger posting included in the event's transport projection.</summary>
public sealed record MarketEconomyPostingMessage(
    Guid AccountId,
    Guid CurrencyId,
    decimal Delta);

/// <summary>Events observed by one host-registered market institution participant.</summary>
public sealed record MarketEconomyParticipantSnapshot(
    string ParticipantId,
    string RoleId,
    IReadOnlyList<MarketEconomyEventMessage> ReceivedEvents);

/// <summary>
/// Result of hosting the Economy reference scenario through ServerApp's in-process message fabric.
/// Economy remains authoritative for all economic state and ledger receipts.
/// </summary>
public sealed record HostedMarketEconomyResult(
    MarketEconomyScenarioResult Economy,
    IReadOnlyDictionary<string, MarketEconomyParticipantSnapshot> Participants,
    IReadOnlyList<SimulationMessageTrace> FabricTrace);

/// <summary>
/// Runs the deterministic Economy scenario and routes its ledger-event projections to
/// host-approved institution participants. This is an event-distribution proof, not an
/// interactive bank, distributed broker, or production financial system.
/// </summary>
public static class HostedMarketEconomySimulation
{
    public const string MessageType = "economy.ledger-event";
    public const int MessageSchemaVersion = 1;
    public const string RunId = "market-economy-reference-v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Executes the Economy scenario, then delivers each authoritative event to one role.</summary>
    public static async ValueTask<HostedMarketEconomyResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Economy owns the ledger and event semantics. ServerApp only transports projections.
        var economy = MarketEconomyScenario.Run();
        var fabric = new SimulationMessageFabric(RunId);
        var participants = new Dictionary<string, MarketInstitutionParticipant>(StringComparer.Ordinal)
        {
            ["treasury"] = new("treasury", "central-bank-treasury"),
            ["employer"] = new("employer", "business-employer"),
            ["bank-clearing"] = new("bank-clearing", "commercial-bank-clearing"),
            ["bank-reserves"] = new("bank-reserves", "commercial-bank-reserves"),
            ["household"] = new("household", "household"),
            ["merchant"] = new("merchant", "merchant")
        };

        fabric.Register(new MarketInstitutionParticipant("economy-ledger", "economy-ledger"));
        foreach (var participant in participants.Values)
            fabric.Register(participant);

        long step = 0;
        foreach (var item in economy.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var recipientId = ResolveRecipient(item.Kind);
            var projection = new MarketEconomyEventMessage(
                item.OperationId,
                item.Kind.ToString(),
                item.Amount.Value,
                item.Amount.CurrencyId.Value,
                economy.Currency.Symbol,
                item.OccurredAt,
                item.LedgerReceipt.Kind.ToString(),
                Array.AsReadOnly((item.LedgerReceipt.Postings ?? Array.Empty<SimulationLedgerPosting>())
                    .Select(posting => new MarketEconomyPostingMessage(
                        posting.AccountId, posting.CurrencyId.Value, posting.Delta))
                    .ToArray()));

            var payload = JsonSerializer.Serialize(projection, JsonOptions);
            var deliveryStep = checked(++step);
            var envelope = new SimulationMessageEnvelope(
                item.OperationId,
                RunId,
                "economy-ledger",
                recipientId,
                MessageType,
                MessageSchemaVersion,
                deliveryStep,
                checked(deliveryStep + 1),
                payload);

            var accepted = fabric.Enqueue(envelope);
            if (accepted.Disposition != SimulationMessageDisposition.Accepted)
                throw new InvalidOperationException($"Economy event {item.OperationId} was not accepted by the fabric.");

            var delivery = await fabric.AdvanceToAsync(deliveryStep, cancellationToken).ConfigureAwait(false);
            if (delivery.Count != 1 ||
                delivery[0].Disposition != SimulationMessageDisposition.Delivered)
                throw new InvalidOperationException($"Economy event {item.OperationId} was not delivered to {recipientId}.");
        }

        var snapshots = participants.ToDictionary(
            pair => pair.Key,
            pair => new MarketEconomyParticipantSnapshot(
                pair.Value.ParticipantId,
                pair.Value.RoleId,
                Array.AsReadOnly(pair.Value.ReceivedEvents.ToArray())),
            StringComparer.Ordinal);

        return new HostedMarketEconomyResult(
            economy,
            new ReadOnlyDictionary<string, MarketEconomyParticipantSnapshot>(snapshots),
            fabric.Trace);
    }

    private static string ResolveRecipient(MarketEconomyEventKind kind) => kind switch
    {
        MarketEconomyEventKind.CurrencyIssued => "treasury",
        MarketEconomyEventKind.BusinessCapitalized => "employer",
        MarketEconomyEventKind.HouseholdEndowed => "household",
        MarketEconomyEventKind.WageAcceptedForClearing => "bank-clearing",
        MarketEconomyEventKind.WageSettled => "household",
        MarketEconomyEventKind.BankFeePosted => "bank-reserves",
        MarketEconomyEventKind.PurchaseSettled => "merchant",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "The market event has no registered recipient.")
    };

    private sealed class MarketInstitutionParticipant(string participantId, string roleId)
        : ISimulationMessageParticipant
    {
        public string ParticipantId { get; } = participantId;
        public string RoleId { get; } = roleId;
        public IReadOnlyCollection<string> AcceptedMessageTypes { get; } = [MessageType];
        public List<MarketEconomyEventMessage> ReceivedEvents { get; } = [];

        public ValueTask ReceiveAsync(
            SimulationMessageEnvelope message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (message.SchemaVersion != MessageSchemaVersion)
                throw new InvalidOperationException("The market participant does not support this message schema.");

            var projection = JsonSerializer.Deserialize<MarketEconomyEventMessage>(message.Payload, JsonOptions)
                ?? throw new InvalidOperationException("The market event payload was empty.");
            if (projection.OperationId != message.MessageId)
                throw new InvalidOperationException("The message identity does not match the Economy operation identity.");
            ReceivedEvents.Add(projection);
            return ValueTask.CompletedTask;
        }
    }
}
