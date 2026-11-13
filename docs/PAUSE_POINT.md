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
- `src/ServerApp.Core/GameSessionContracts.cs`: game-neutral `IGameSessionAdapter` contract and process-local `GameSessionCoordinator`. The host assigns session IDs, pins adapter version to the immutable match assignment, requires explicit Ready, rejects duplicate match starts and illegal/stale lifecycle transitions, and records Aborted/Failed outcomes on cancellation or stop. No SC2-specific process/client adapter or durable session history is implemented yet.
- `tests/ServerApp.ContractChecks/Program.cs`: 33 core contract checks cover manifest/provisioning, placement, competition contracts, and 1v1 admission without a DataWarehouse dependency.
- `tests/ServerApp.DataWarehouse.ContractChecks/`: 14 optional integration checks cover fixed-record catalogue behavior, scale/concurrency, durable-first visibility, restart reconstruction from a store snapshot, write failure, eligibility persistence, and stale-revision recovery.

## Durability boundary — database-free file storage

- **Architecture decision: no databases.** ServerApp will not introduce a database engine, database service, ORM, or database dependency for the bot-build registry. Storage is optimized for runtime performance and simple recovery, not minimum disk consumption.
- `src/ServerApp.FileStore/FileBotBuildRegistryStore.cs` now implements the concrete host-owned file store behind `IBotBuildRegistryStore`, with no database or third-party package dependency. Each revision is a checksummed binary journal frame containing only changed registrations and removals. Writer arbitration uses an exclusive lock file; expected revision is checked after acquiring the lock; a complete frame is flushed before `Committed` is returned.
- `LoadAsync` validates the checkpoint and every complete journal frame, replays strictly sequential revisions, truncates only an incomplete final frame, and fails closed on complete bad framing/checksum or unsupported schema. `CompactAsync` writes and flushes a full checkpoint, atomically installs it, then truncates the journal; replay safely skips old journal frames if a crash occurs between checkpoint installation and journal truncation.
- `tests/ServerApp.FileStore.ContractChecks/` adds seven disk-backed checks for restart, deltas/removals, competing instances, torn tails, corruption, compaction, and schema rejection. CI now builds and runs this project explicitly. The new suite has not yet run on GitHub Actions, so implementation is not yet a verified durability proof.
- DataWarehouse remains the fast process-local hot catalogue. The host-owned file store is authoritative; startup validates its checkpoint/journal and rebuilds DataWarehouse shelves. Never persist DataHandle values, shelf slot indexes, or native addresses as durable identity.
- File-store commits must arbitrate writers, verify the expected store revision, durably flush a complete checksummed journal record before reporting Committed, and never throw after a commit has taken effect. A revision conflict requires reload before retry.

## Verification and sequencing

- Earlier evidence remains valid only for its cited revisions: ServerApp CI run [#43184712816](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp/actions/runs/43184712816) passed for the earlier DataWarehouse integration revision, and Pages run [#43184712477](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp/actions/runs/43184712477) passed for its corresponding revision.
- The current authored suites contain 61 checks: 40 dependency-free Core checks, 14 DataWarehouse integration checks (including file-backed registry/DataWarehouse restart reconstruction), and 7 standalone file-store checks. CI builds and runs the two optional integration projects explicitly. The latest development revision has not yet been verified by CI; do not describe these suites as passing until the relevant run completes.
- Sith policy is a publish/release gate, not a development/execution blocker. Extra branch cleanup remains owner-controlled.
- PR #3 remains draft, open, unmerged, and contains candidate-scoped composition/lifecycle work not yet on `development`. Do not duplicate that work or merge it without explicit authorization. Issue #7 lifecycle sequencing remains gated on PR #3.
- Issue #9 tracks durable persistence/recovery: [Persist and recover the DataWarehouse-backed bot-build catalogue](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp/issues/9).

## Resume sequence

1. Preserve direct development-branch work; inspect current source, tests, docs, workflows, and commit evidence before each implementation slice.
2. Keep the core host and its contract checks dependency-free. Build and verify DataWarehouse only through its explicit integration project and dedicated checks.
3. Run the new file-store contract suite in CI; fix compile/runtime failures before layering more behavior on top. Add failure-injection coverage for append interruption and checkpoint replacement, plus malformed/duplicate snapshot payloads and cross-process locking where the CI environment permits.
4. Review durability semantics on Windows and Linux, especially exclusive lock behavior, flush guarantees, and atomic checkpoint replacement. Do not claim stronger filesystem guarantees than the runtime/platform actually provides.
5. Integrate `FileBotBuildRegistryStore` into the ServerApp host configuration path so the configured server role opens the authoritative registry and rebuilds DataWarehouse before accepting registrations/admissions. Keep storage path and capacity in trusted host configuration, not client input.
6. Move StarCraft II-specific ladder composition into a separate experience/application repository, delivered as versioned MicroBundles selected by a ServerApp role manifest. ServerApp keeps generic competition hosting, registry, lifecycle, and result contracts; it does not own SC2 rules or bot strategy.
7. Continue the 1v1 vertical slice only after the store/recovery boundary and host integration are proven, then add SC2 game-session supervision and authoritative result intake.
8. Keep 2v2, FFA, 3v3, 4v4, ratings, and multi-host operation staged behind real 1v1 evidence.

## Platform invariants

- FSM_COS owns composition; FSM_API provides state/context/process-group primitives; FSM_Foundry owns host workflow execution/lifecycle.
- ServerApp provisions and operates server roles and composed applications; it does not become a second FSM_COS or absorb SingularityBot strategy.
- Preserve exact bot build identity and SHA-256 content provenance through admission and match records.
- The game-session host/adapter—not a bot's own claim—is the authoritative source of terminal match evidence.
- The bot-build registry now has a source implementation of file persistence; do not claim production durability until the new disk-backed checks pass and host startup/registration are wired to this store. Do not claim matchmaking, SC2 hosting, authoritative results, rating calculations, horizontal scaling, or live ladder availability until implemented and tested.

---
<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="200" /></p>
