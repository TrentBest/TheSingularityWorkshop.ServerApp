# Hosted Market-Economy Simulation

**Maturity: implemented; CI verification pending.** This is the first ServerApp-hosted simulation adapter. It runs the deterministic fictional market scenario owned by Economy and routes each authoritative ledger event through ServerApp's in-process simulation message fabric.

## Ownership boundaries

- **Economy** owns the simulated currency, accounts, ledger operations, event semantics, balances, and authoritative ledger receipts.
- **ServerApp.Core** owns host-neutral participant registration, message validation, deterministic virtual-time delivery, and fabric tracing. It has no Economy dependency.
- **ServerApp.Economy** is the integration adapter. It consumes the Economy scenario and projects its ledger events into versioned messages for registered institution participants.
- The integration does not recreate or mutate ledger balances. The Economy scenario is executed once; ServerApp transports projections of its event receipts.

The CI workflow checks out Economy at the explicit development source revision `a62850001cbfc9e41c07ae1533d64d700542230b`. Updating that dependency pin is a deliberate integration change, not an implicit package upgrade.

## Participants and routing

The initial closed scenario emits seven deterministic ledger events. Each event is delivered to one role projection:

| Economy event | ServerApp participant |
|---|---|
| CurrencyIssued | treasury |
| BusinessCapitalized | employer |
| HouseholdEndowed | household |
| WageAcceptedForClearing | bank-clearing |
| WageSettled | household |
| BankFeePosted | bank-reserves |
| PurchaseSettled | merchant |

The message ID is the Economy operation ID. The envelope uses run ID `market-economy-reference-v1`, message type `economy.ledger-event`, schema version 1, and monotonically increasing virtual delivery steps. Payloads carry the event kind, amount/currency identity, timestamp, ledger operation kind, and signed posting projections derived from the receipt's source/destination account IDs. Issuance is represented as a one-sided positive posting; transfers are represented as balanced negative/positive postings.

Each participant validates the schema and operation/message identity before recording the received projection. The result includes the original Economy scenario result, immutable participant snapshots, and the fabric trace.

## Running the proof

The adapter is exposed as:

```csharp
var result = await HostedMarketEconomySimulation.RunAsync();
```

The ServerApp contract-check executable verifies:
- all seven Economy events are accepted and delivered exactly once in this run;
- every event reaches its intended institution participant;
- the received operation IDs match the Economy ledger receipts;
- payloads retain currency identity and signed posting details;
- independent runs produce the same balances, participant histories, and delivery trace.

## Explicit limits

This is an in-process event-distribution proof, not an interactive bank simulation. The underlying market scenario remains a small, closed fictional ledger flow. It does not model commercial-bank balance sheets, deposit creation, lending, interest, securities, order books, price discovery, external settlement, or real-world custody. The fabric remains process-local, volatile, sequential, and single-host; there is no durable broker, retry, cross-process delivery, or exactly-once guarantee.

No real money, external market data, payment integration, public endpoint, or physical actuation is involved. No package publication or deployment is part of this proof.

## Source and tracking

- [Integration source](../src/ServerApp.Economy/HostedMarketEconomySimulation.cs)
- [Economy market scenario](https://github.com/TrentBest/TheSingularityWorkshop.Economy/blob/development/src/TheSingularityWorkshop.Economy/MarketEconomyScenario.cs)
- [Simulation message fabric](../src/ServerApp.Core/SimulationMessageFabric.cs)
- [ServerApp issue #18](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp/issues/18)
- [Economy issue #30](https://github.com/TrentBest/TheSingularityWorkshop.Economy/issues/30)

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="200" /></p>
