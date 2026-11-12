<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Competitive Ladder Composition — Architecture Direction

**Status: architecture proposal and Pages demonstration. No live ladder, matchmaking service, StarCraft II host, bot admission endpoint, authoritative result service, or rating service is implemented by this document.**

## 1. Product boundary

The competitive ladder is an application composed on ServerApp. It is not the definition of ServerApp.

- **ServerApp** provides the hosting and operational platform: role provisioning, admission boundaries, capacity placement, routing, lifecycle, health, and the services required by a composed application.
- **The competitive system** is one application assembled from those capabilities: bot identity/build registration, queues, match assignment, match records, rating projections, and creator/team relationships.
- **StarCraft II** is the first external game integration and demonstration workload. It must not become a special-case execution engine inside ServerApp.
- **SingularityBot** is a game-playing client/runtime. It owns its game strategy, normalized observations, intents, and validated SC2 actions. It is not the ladder, matchmaking service, or authoritative result source.
- **FSM_COS** composes approved MicroBundles into a RuntimeAssembly.
- **FSM_Foundry** executes the host-authored workflow and owns workflow lifecycle.
- **FSM_API** supplies the state/context execution primitives used by the composed runtime.

The existing [Fleet Lab](https://trentbest.github.io/TheSingularityWorkshop.ServerApp/) remains the general fleet/platform demonstration. The [Competitive Ladder Lab](https://trentbest.github.io/TheSingularityWorkshop.ServerApp/competitive-ladder/) demonstrates one composed application. Both are static GitHub Pages presentations, not service endpoints.

## 2. Keep the contracts distinct

ServerApp already distinguishes a ServerRoleManifest (one requested operational server-role instance) from FSM_COS's RuntimeManifest (the MicroBundles to compose) and Foundry's WorkflowDefinition (the host-authored sequence of operations). A competitive system adds domain contracts; it must not overload any of those three.

The following are proposed domain concepts, not existing source types:

| Concept | Purpose |
|---|---|
| CompetitionDefinition | Identifies a game, ruleset revision, supported match formats, eligibility policy, and result policy. |
| MatchFormat | Describes participant slots and team topology: 1v1, 2v2, FFA, 3v3, 4v4, and later formats. |
| BotIdentity | Stable creator-owned identity independent of any particular release. |
| BotBuild | Immutable version/content identity for the exact bot runtime admitted to a match. |
| TeamAssignment | Match-scoped association between participants; it does not transfer bot ownership. |
| MatchAssignment | Immutable snapshot of format, ruleset, participants, bot builds, host allocation, and adapter versions. |
| GameSession | The external game instance and its observable lifecycle/readiness/termination evidence. |
| MatchRecord | Authoritative terminal outcome plus the evidence and event history that support it. |
| RatingProjection | A derived ladder view calculated from accepted match records and a versioned rating policy. |

The first typed, versioned contracts now exist in `ServerApp.Core/CompetitionContracts.cs`: `CompetitionDefinition`, `CompetitionMatchFormat`, `BotBuildReference`, `MatchParticipantAssignment`, and immutable `MatchAssignment`. The validator pins game/ruleset/adapter versions and bot-build SHA-256 identities, rejects duplicate bot identities and unsupported team topology, and snapshots caller-owned collections. The initial topology vocabulary covers 1v1, 2v2, FFA, 3v3, 4v4, and 2v2v2. These are domain contracts and validation only—not a live catalogue, admission endpoint, persistence layer, or matchmaking service. Do not pre-build a broad tournament schema for hypothetical modes.

## 3. Ownership and trust boundaries

### ServerApp application roles

A future competitive application may be composed from separately registered roles such as:

- competition catalogue and ruleset registry;
- bot/build admission and eligibility;
- queue and matchmaker;
- match coordinator;
- game-session supervisor/adapter;
- result intake and validation;
- match-history store;
- rating projection;
- creator/team coordination gateway;
- management and observation surfaces.

These are role candidates, not a claim that the roles currently exist. They may be co-located initially. The host must continue to use explicit host-registered role factories and bounded manifests; client input must not select executable code, assembly paths, arbitrary workflow handlers, or shell commands.

### Bot and creator ownership

1. A bot identity belongs to its creator; each build is a separate immutable release identity.
2. A match pins the exact build admitted. A mutable label such as latest may be used during admission, but the resolved build must be snapshotted before launch.
3. A team is a match-scoped grouping of independently owned participants.
4. Cross-creator teams are valid when the competition's ruleset permits them. Team membership does not merge accounts, ownership, source, secrets, or release authority.
5. Any bot-to-bot coordination must use an explicit, versioned, bounded team communication contract. It must not become an unrestricted side channel or grant one bot authority over another creator's runtime.

### Result authority

- A bot's own message is never sufficient evidence that it won.
- The game-session host/adapter is the trusted source of game lifecycle and terminal outcome evidence.
- Result intake validates the session identity, assignment snapshot, game/ruleset adapter version, terminal state, and duplicate/idempotency conditions before accepting a result.
- Match records preserve the evidence and policy versions used to accept the outcome.
- Ratings are derived from accepted records. They are not mutable values that bots can write.

Authentication, creator verification, private build provenance, anti-cheat policy, retention, and operational authorization remain explicit product/security decisions. Do not silently treat a public Pages visitor or a bot process as trusted.

## 4. Game integration is not bot execution

There are two distinct SC2-facing responsibilities:

1. **Game-session integration:** creates or attaches to a game session, owns session lifecycle, enforces the match's game settings, observes termination, and produces trusted result evidence.
2. **Bot SC2 adapter:** connects the bot runtime to the game protocol, translates observations into normalized facts, and translates validated bot intents into protocol actions.

SingularityBot's architecture already places the SC2 protocol adapter at the edge of the bot. The rest of the bot consumes protocol-independent facts and emits intents; the action pipeline validates those intents before submission. ServerApp should host/coordinate the game-session responsibility, not absorb the bot's strategy or protocol adapter.

A future external game process may need a child-process boundary, bootstrap contract, authenticated IPC, health/readiness protocol, resource limits, and observable termination. The first ServerApp supervised-role milestone in issue #7 is an in-process net8.0 component proof; it is not permission to pretend an external SC2 process can be safely hosted without that separate boundary.

## 5. First proof: 1v1

The first real vertical slice should be deliberately narrow:

1. Admit two distinct bot identities and resolve each to an immutable, eligible build.
2. Resolve one versioned 1v1 competition/ruleset and its game-session configuration.
3. Create an immutable match assignment before starting work.
4. Allocate a compatible game-session host role through ServerApp's existing provisioning boundary.
5. Compose each approved bot runtime through its own host-controlled FSM_COS composition session.
6. Execute the host-authored Foundry workflow; keep live RuntimeAssembly and process references in host-owned state, never in bounded JSON workflow context.
7. Establish explicit readiness for both bot participants and the game session before the match becomes active.
8. Observe the authoritative game terminal state; validate and persist the result exactly once.
9. Derive the ladder/rating projection from accepted match records.
10. Preserve enough event/decision/replay metadata to reproduce and investigate the result.

A failure at any stage must not create a false Ready, start a partially composed match, or award a result. Retry and cleanup semantics must be explicit and idempotent.

## 6. Expand formats without changing the platform

Use a generic participant collection plus an optional team assignment from the beginning. 1v1 is represented as two one-member teams; 2v2 as two teams of two; FFA as independent participants; 3v3 and 4v4 as larger teams. This avoids making PlayerA and PlayerB permanent schema fields.

| Stage | New proof required |
|---|---|
| 1v1 | Two independent bots, one authoritative game session, one accepted result. |
| 2v2 | Team assignment, team eligibility, and cross-creator membership. |
| FFA | Multiple independent participants and placement/tie semantics. |
| 3v3 / 4v4 | Larger admission sets, team coordination, capacity and failure handling. |
| Multi-host | Measured placement, routing, isolation, recovery, and session continuity. |

Do not assume every game supports every format. A CompetitionDefinition advertises only combinations supported by the selected game adapter and ruleset.

## 7. Sequencing and current evidence

The order matters because ServerApp should not claim a live competitive system before both the hosting boundary and the game-playing workload are real.

1. **Preserve the current composition boundary.** PR #3 contains candidate-scoped FSM_COS sessions, verified immutable artifact preparation, and a host-owned lifecycle controller. The owner recorded that issue #7 must wait until PR #3 reaches development. Do not duplicate that work or merge the PR without the owner's explicit direction.
2. **Prove a supervised role component.** Resume ServerApp issue #7 after its sequencing gate. Its current design decision is an in-process supervised net8.0 component with explicit readiness, bounded drain, observed termination, and retry-safe cleanup. This is generic host infrastructure, not the final SC2 process isolation design.
3. **Prove the bot locally.** The SingularityBot roadmap prioritizes a playable local Terran opening before ladder integration. Its architecture and strategy schema are present, and its roadmap records 166 passing tests on the cited baseline; that CI evidence is not a live SC2 match or proof of ladder readiness. Require a match-verified local game before treating the bot as an admitted competitive participant.
4. **Prove one local 1v1 session.** Connect the real game-session adapter and two independently admitted bot builds without exposing a public control endpoint.
5. **Add authoritative result persistence and rating projections.** Keep result acceptance separate from bot output and keep the rating algorithm versioned.
6. **Add queueing and admission policy.** Queue semantics should follow from actual match/session constraints, not precede them.
7. **Expand team formats and multi-host placement.** Measure capacity, isolation, startup, readiness, throughput, latency, and recovery before claiming scale.

### Current state

- **Implemented:** ServerApp's local role-manifest validation, deterministic dependency planning, in-memory capacity placement, Foundry workflow integration, source-level competitive definition/assignment contracts, a host-independent 1v1 admission service, and a DataWarehouse-backed fixed-record bot-build catalogue. The catalogue stores immutable SHA-256 payload identity and mutable eligibility in a `DataShelf<BotBuildSlot>`, serializes access at the adapter boundary, supports incremental dirty snapshots with revision-safe acknowledgements, and exposes domain snapshots without leaking handles or native addresses. Each admission request names an exact bot identity and immutable build ID. Admission resolves that exact pair through an injected `IRegisteredBotBuildCatalog`, accepts only builds in the `Eligible` state, rejects pending-review/suspended/revoked or substituted builds, verifies identity/build ownership and SHA-256 integrity, and produces a validated immutable assignment. The domain exposes eligibility vocabulary and a host-owned `IBotBuildRegistryStore` snapshot/CAS contract. Durable snapshots carry explicit schema version 1; host stores must migrate supported older serialized formats before returning the current contract and reject unknown future versions. `DurableBotBuildRegistry` prepares a replacement DataWarehouse catalogue, asks the host store to atomically commit the next revision, and swaps the visible hot catalogue only after a successful durable commit. A failed write or revision conflict leaves the prior catalogue visible; `ReloadAsync` refreshes a stale instance from the authoritative store before retry. The contract does not choose a persistence technology or implement registration/review authorization. PR #3's candidate-scoped composition/lifecycle work remains open and is not on `development`.
- **Previously verified baseline:** ServerApp CI run [#43184712816](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp/actions/runs/43184712816) passed on an earlier revision with the DataWarehouse integration, 4,096-build and concurrent-caller checks, and 40 Core contract checks. Pages run [#43184712477](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp/actions/runs/43184712477) passed for its corresponding earlier revision.
- **Current verification state:** 45 checks have been separated into 33 dependency-free Core checks and 12 optional DataWarehouse integration checks. CI now runs the integration project explicitly after checking out its pinned DataWarehouse source. The dependency split and latest durable-first coordinator revision have not yet been verified by CI; do not transfer the earlier pass result to this revision.
- **Demonstrated:** the Pages UI can express match formats, participants, creator ownership, build policy, and architecture boundaries as a local simulation.
- **Not implemented:** a concrete durable bot/build registration store and its crash-safe implementation, durable competition catalogue, matchmaking or queueing, SC2 game-session hosting, authenticated participant transport, authoritative result intake, persistent match history, rating calculations, team communication, or public ladder service. The durable-store contract and coordinator are implemented, with contract tests using an in-memory failure-injection store; this is not evidence of disk/database durability or crash-safe storage. The DataWarehouse catalogue remains deliberately process-local: DataWarehouse does not currently promise persistence or shared-shelf concurrent mutation. A host must supply an atomic durable store and rebuild the hot catalogue on startup. The catalogue does not decide review authorization, queue participants, or launch games.
- **Not yet match-verified:** SingularityBot as a ladder-ready SC2 participant.

Do not promote any of the last three categories to implemented based on a static simulation, schema validation, unit tests, or a successful Pages deployment.

## 8. Related implementation references

- [ServerApp architecture](ARCHITECTURE.md)
- [Foundry integration boundary](FOUNDRY_INTEGRATION.md)
- [Server role manifest](SERVER_ROLE_MANIFEST.md)
- [Server role lifecycle issue #7](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp/issues/7)
- [Persist and recover the DataWarehouse-backed bot-build catalogue issue #9](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp/issues/9)
- [DataWarehouse source and storage contract](https://github.com/TrentBest/TheSingularityWorkshop.DataWarehouse)
- [Candidate-scoped composition PR #3](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp/pull/3)
- [SingularityBot architecture](https://github.com/TrentBest/TheSingularityWorkshop.SingularityBot/blob/development/docs/architecture.md)
- [SingularityBot strategy contract](https://github.com/TrentBest/TheSingularityWorkshop.SingularityBot/blob/development/docs/strategy-format.md)
- [SingularityBot roadmap](https://github.com/TrentBest/TheSingularityWorkshop.SingularityBot/blob/development/docs/roadmap.md)

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="200" /></p>
