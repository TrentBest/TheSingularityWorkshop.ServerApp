# Deterministic Simulation Message Fabric

**Maturity: source-implemented; CI contract checks passed on commit `5d1a1680a45dad0fc0705e5a97f18adf4e59437f`.** The first ServerApp simulation transport is an in-process, deterministic message fabric intended for local proofs and simulations. It is not a distributed broker or production transport.

## Contract

A host registers each `ISimulationMessageParticipant` explicitly. Registration trims and validates participant/role identifiers and snapshots a normalized finite accepted-message vocabulary. IDs and message types that collide after normalization are rejected. Each participant owns its own state and validates the content of every message it receives.

A `SimulationMessageEnvelope` carries:
- stable message and simulation-run IDs;
- sender and recipient participant IDs;
- a message type and positive schema version;
- earliest delivery step and exclusive expiry step;
- a bounded string payload.

The fabric rejects messages from unknown senders, to unknown recipients, for another run, or scheduled before current virtual time. A recipient rejects message types outside its registered vocabulary.

## Ordering and duplicate behavior

Messages are delivered sequentially by:
1. earliest eligible virtual step; then
2. the fabric's monotonically assigned enqueue sequence.

Repeating an identical message ID and content returns `Duplicate` without a second enqueue. Reusing the ID for different content is rejected. Delivery is not automatically retried. Handler failures are recorded and isolated so later independent messages can still be delivered.

A message expires when the requested simulation step is greater than or equal to its `ExpiresAfterStep`. Advancing virtual time backwards is rejected. Virtual steps are simulation time, not wall-clock time, a real-time scheduler, or a second FSM_API heartbeat.

## Evidence and limits

`SimulationMessageFabric.Trace` exposes immutable observations for accepted, duplicate, rejected, delivered, expired, and failed messages. The contract checks cover deterministic ordering, delayed delivery, duplicate identity, changed-payload rejection, expiry, unknown participants, recipient vocabulary enforcement, handler failure isolation, and time monotonicity.

The current implementation is deliberately:
- process-local and volatile;
- single-host and in-memory;
- sequential rather than concurrent;
- without network listeners, durable buffering, broker quorum, retries, or cross-process guarantees;
- not an exactly-once delivery system.

The fabric transports messages; it does not authorize economic operations, validate ledger invariants, or declare transactions settled. Economy retains those responsibilities. A later distributed transport must separately design identity, authorization, durable acknowledgement, backpressure, partitions, causal ordering, retry/idempotency, quotas, and operational observability.

Source: [SimulationMessageFabric.cs](../src/ServerApp.Core/SimulationMessageFabric.cs). Tests: [ServerApp contract checks](../tests/ServerApp.ContractChecks/Program.cs). Tracking: [ServerApp issue #15](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp/issues/15), [Economy issue #30](https://github.com/TrentBest/TheSingularityWorkshop.Economy/issues/30).

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="200" /></p>
