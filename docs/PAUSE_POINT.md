<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# ServerApp Pause Point

## Current checkpoint — 2026-11-12

- Repository: [TheSingularityWorkshop.ServerApp](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp).
- Active work branch: `development`; `master` remains the released/stable baseline.
- Branch inventory last inspected: `development`, `master`, and the pre-existing `work/candidate-scoped-composition-sessions` branch. The owner controls branch deletion.
- Current workstream: StarCraft II competitive ladder hosting, with DataWarehouse deliberately adopted as the hot in-memory catalogue technology to showcase and harden it.
- ServerApp owns hosting/competition operations; SingularityBot remains independent player intelligence. FSM_COS composes approved MicroBundles, FSM_Foundry executes host-authored workflows, and FSM_API remains the state/context/process-group primitive.
- No NuGet publication, merge to `master`, external release, production deployment, public ladder endpoint exposure, or branch deletion is authorized by this checkpoint.

## Competitive ladder implementation

- `src/ServerApp.Core/CompetitionContracts.cs`: competition definitions, match formats, immutable bot-build references, match participant assignments, and fail-closed assignment validation. Initial topology vocabulary includes 1v1, 2v2, FFA, 3v3, 4v4, and 2v2v2.
- `src/ServerApp.Core/MatchAdmissionService.cs`: host-independent 1v1 admission resolves exact bot identity + immutable build ID, accepts only Eligible builds, and snapshots the selected build identities before launch. It does not queue, launch, or rate matches.
- `src/ServerApp.DataWarehouse/DataWarehouseBotBuildCatalog.cs`: DataWarehouse-backed fixed-record hot catalogue. Shelf access is serialized at the adapter boundary; handles/native addresses do not escape; dirty snapshots and revision-safe acknowledgements are supported. This is process-local hot storage, not durability.
- `src/ServerApp.Core/BotBuildRegistryStoreContracts.cs`: immutable domain snapshot and host-owned atomic compare-and-swap store contract. Every snapshot construction must supply schema version 1 explicitly; stores must reject missing metadata, migrate supported older payloads, and reject unknown versions. Revision tokens are store-wide versions, not DataWarehouse handles.
- `src/ServerApp.DataWarehouse/DurableBotBuildRegistry.cs`: candidate catalogue is built and validated off to the side, the host store commits first, and only then is the replacement hot catalogue made visible. Failed writes and revision conflicts leave the previous catalogue visible; stale instances can call `ReloadAsync`.
- `tests/ServerApp.ContractChecks/Program.cs`: 33 core contract checks cover manifest/provisioning, placement, competition contracts, and 1v1 admission without a DataWarehouse dependency.
- `tests/ServerApp.DataWarehouse.ContractChecks/`: 13 optional integration checks cover fixed-record catalogue behavior, scale/concurrency, durable-first visibility, restart reconstruction from a store snapshot, write failure, eligibility persistence, and stale-revision recovery.

## Durability boundary — database-free file storage

- **Architecture decision: no databases.** ServerApp will not introduce a database engine, database service, ORM, or database dependency for the bot-build registry. Storage is optimized for runtime performance and simple recovery, not minimum disk consumption.
- The coordinator and host-owned store contract are implemented; a concrete file store remains to be implemented and verified.
- The intended format is a compact binary, append-oriented journal with checksummed revision records and periodic compact checkpoints. Normal registration/eligibility mutations should append only the changed record rather than rewrite the entire catalogue. Startup loads the latest checkpoint and replays the journal; compaction is an explicit maintenance operation.
- The in-memory failure-injection store in contract checks validates coordination semantics only. It does not prove disk durability, crash recovery, filesystem locking, torn-write handling, or atomic checkpoint replacement.
- DataWarehouse remains the fast process-local hot catalogue. The host-owned file store is authoritative; startup validates its checkpoint/journal and rebuilds DataWarehouse shelves. Never persist DataHandle values, shelf slot indexes, or native addresses as durable identity.
- File-store commits must arbitrate writers, verify the expected store revision, durably flush a complete checksummed journal record before reporting Committed, and never throw after a commit has taken effect. A revision conflict requires reload before retry.

## Verification and sequencing

- Earlier evidence remains valid only for its cited revisions: ServerApp CI run [#43184712816](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp/actions/runs/43184712816) passed for the earlier DataWarehouse integration revision, and Pages run [#43184712477](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp/actions/runs/43184712477) passed for its corresponding revision.
- The dependency split preserves the 46 authored checks as 33 core checks plus 13 DataWarehouse integration checks. The updated CI workflow now builds/runs the optional integration separately after checking out its pinned source. This revision has not yet been verified by CI; do not describe either suite as passing until CI executes on the relevant commit.
- Sith policy is a publish/release gate, not a development/execution blocker. Extra branch cleanup remains owner-controlled.
- PR #3 remains draft, open, unmerged, and contains candidate-scoped composition/lifecycle work not yet on `development`. Do not duplicate that work or merge it without explicit authorization. Issue #7 lifecycle sequencing remains gated on PR #3.
- Issue #9 tracks durable persistence/recovery: [Persist and recover the DataWarehouse-backed bot-build catalogue](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp/issues/9).

## Resume sequence

1. Preserve direct development-branch work; inspect current source, tests, docs, workflows, and commit evidence before each implementation slice.
2. Keep the core host and its contract checks dependency-free. Build and verify DataWarehouse only through its explicit integration project and dedicated checks.
3. Implement the concrete database-free file store behind `IBotBuildRegistryStore`; the architecture decision is settled. Keep it in an isolated host-storage project so the default ServerApp solution and DataWarehouse remain independently consumable.
4. Use framed binary journal records with explicit schema/revision metadata and checksums; serialize cross-process writers, compare the authoritative revision under that lock, and durably flush before returning `Committed`. Recover an incomplete final frame as a torn tail, but fail closed on a complete frame with invalid framing/checksum or unsupported schema. Add compact checkpoints and safe journal rotation only after append/replay semantics are proven.
5. Verify restart recovery, interrupted writes, corruption, duplicate keys, capacity, multi-instance/process CAS, checkpoint replacement, and failure injection. The current domain contract rejects unknown schema versions; it does not itself migrate serialized files.
6. Run the complete ServerApp CI and all contract-check suites against the resulting revision; distinguish source implementation from verified behavior.
7. Continue the 1v1 vertical slice only after the store/recovery boundary is proven, then add the SC2 game-session adapter and authoritative result intake.
8. Keep 2v2, FFA, 3v3, 4v4, ratings, and multi-host operation staged behind real 1v1 evidence.

## Platform invariants

- FSM_COS owns composition; FSM_API provides state/context/process-group primitives; FSM_Foundry owns host workflow execution/lifecycle.
- ServerApp provisions and operates server roles and composed applications; it does not become a second FSM_COS or absorb SingularityBot strategy.
- Preserve exact bot build identity and SHA-256 content provenance through admission and match records.
- The game-session host/adapter—not a bot's own claim—is the authoritative source of terminal match evidence.
- Do not claim persistence, matchmaking, SC2 hosting, authoritative results, rating calculations, horizontal scaling, or live ladder availability until implemented and tested.

---
<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="200" /></p>
