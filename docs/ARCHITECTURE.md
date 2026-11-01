<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# ServerApp Architecture

## 1. Purpose

ServerApp is the Workshop's **manifest-driven server fleet and host platform**. It is intended to turn a declared computational role and its dependencies into a provisioned, observable server capability, using available hardware and the Workshop runtime rather than requiring each server role to be engineered as a bespoke application.

The target is rapid, repeatable provisioning: provide a validated manifest, resolve its dependencies and resource requirements, place it on suitable capacity, activate it, and report readiness. "Instantaneous" is a design aspiration to quantify, not a performance claim. Startup, placement, warm-pool reuse, dependency readiness, and time-to-first-use must be measured independently.

ServerApp is not one monolithic backend. It coordinates a fleet of distinct server roles with explicit ownership and trust boundaries.

## 2. System responsibilities

| System | Established or intended responsibility |
|---|---|
| FSM_COS | Composition kernel: resolves/validates a host-supplied MicroBundle catalog, composes dependencies, performs bounded arbitration, and returns a RuntimeAssembly. |
| FSM_API | State/context and process-group execution primitives. Do not create a competing semantic tick or heartbeat. |\n| Foundry | Owns the ServerApp provisioning workflow lifecycle through the source-pinned host integration. It does not compose FSM_COS runtimes. |
| AnyApp | Desktop host and desktop presentation. |
| WebApp | Browser-facing host and browser presentation; independent of desktop AnyApp. |
| ServerApp | Server fleet provisioning, role hosting, routing/coordination boundaries, capacity placement, and server operational lifecycle. |
| Renderer | Reduce the amount of server-side work required to communicate visual state; exact protocol and ownership must be proven against the Renderer implementation. |
| Domains | Provide external-facing domain facades for Experiences, including creator-selected presentation such as a shop-like facade. A domain is not merely a server address. |
| Experience | The hosted interactive content/stateful world. It must remain distinct from the infrastructure that hosts it. |

## 3. Server fleet model

Server roles are independently defined and may be deployed separately, co-located when safe, or scaled horizontally. The following is the current product vision, not a claim that these services exist yet.

### 3.1 Landing-experience hosts

A landing host serves the player's entry experience and the transfer points to other experiences. The default presentation may be a garden, but the landing experience is not restricted to that aesthetic. A maze, for example, could make players navigate to discover its gates or portals.

A valid landing experience must clearly denote and provide interactable access to these required destinations:

- **Social**
- **Settings**
- **Hangouts**
- **Exit**
- **Domains**
- **Workshop**

These are semantic destinations, not necessarily six fixed visual buttons. They must be discoverable and operable as interactables in the experience. A maze or other creative layout may change how players find them, but may not silently omit or make required destinations inaccessible. Each transfer point changes the active experience/context through an explicit transition contract.

Landing hosts may host many landing experiences when their measured resource profiles fit. Additional hosts are provisioned and traffic redistributed when capacity or latency targets require it.

### 3.2 Small dedicated platform services

The vision includes separately rentable, low-cost service roles such as:

- Settings server
- Social server
- Hangouts server
- Chat server
- Private-business server

These roles should have small, explicit contracts and resource profiles. A low monthly rental is a product goal, not a current price or cost guarantee. Co-location is permitted only when isolation, security, availability, and resource contention remain acceptable.

### 3.3 Workshop and Singularity Central Station

The Workshop is envisioned as an entangled, persistent set of server domains. Singularity Central Station is its navigation/coordination hub: each platform is presented as a train leading to a specific domain. The station and its domains are themselves persistent and entangled rather than disposable landing pages.

This requires explicit identity, domain discovery, authorization, persistence ownership, and transition semantics. The train metaphor is presentation; the underlying platform/domain contract must remain machine-readable and stable.

### 3.4 Domain facades

Domains expose the external facade for Experiences. A creator can select a facade style permitted by the domain—for example, a shop-like presentation. The domain governs what facade capabilities are available and how an Experience is discovered or entered.

Keep these concepts separate:
- Experience: the interactive content and its runtime state.
- Domain: the externally visible identity, policy, and facade.
- Host/server: the compute and service capacity running the relevant workload.
- Manifest: the declared role, dependencies, configuration, and resource requirements used to provision it.

### 3.5 Authentication and client communication path

The proposed login path deliberately separates credential intake, authentication, and ongoing client traffic:

1. A **login/broker server** receives the client's login request and forwards the necessary request data to the authentication authority over a protected service-to-service channel.
2. The **authentication server** validates credentials and policy. The broker must not become a second authentication authority or make approval decisions itself.
3. On approval, the authentication authority issues or authorizes a scoped token. The client receives the token and the permitted communication endpoint/channel details through a defined secure protocol.
4. The client connects to a **client I/O gateway** dedicated to processing inbound and outbound client messages.
5. The gateway routes messages to the responsible experience or platform service based on authenticated identity, session, domain, and workload ownership. It acts as a bounded message intermediary—not as an arbitrary command relay.

Token format, lifetime, audience, revocation, transport, channel negotiation, and service-to-service authentication are open security design items. Never forward raw credentials broadly or trust client-supplied routing claims.

The client I/O gateway can scale horizontally so client communication does not bottleneck on a single process. Gateway scaling and Experience compute scaling are separate dimensions.

### 3.6 Persistent-universe and Experience compute fleet

Persistent universes and MMORPG-like Experiences may require multiple compute hosts. ServerApp should provision enough capacity for current demand, distribute players/workloads, and avoid concentrating too many players in one simulation area or process.

The scheduler should consider more than total player count: spatial density, interaction locality, simulation cost, latency, state ownership, memory, CPU/GPU capability, and migration cost may all matter. Splitting a world across hosts introduces consistency, handoff, authority, and network-boundary problems that must be designed explicitly.

A server may host multiple Experiences when their combined measured profiles fit within its resource and isolation budgets. Otherwise, ServerApp should place or activate another host and distribute work. Scale-out and scale-in policies, warm pools, and minimum capacity are future measurable decisions.

### 3.7 Rendering and delta-delivery servers

A rendering-server role is envisioned to generate image frames, compare successive frames, and transmit a delta when its encoded representation is smaller than sending the complete image. The decision must include framing, compression, transport overhead, decode cost, and recovery/keyframe rules; comparing raw pixel differences alone is not enough.

Renderer is expected to reduce the work the server must perform and/or transmit. This is a promising architectural advantage, but its exact savings must be measured against a defined baseline and workload. Rendering, simulation, and client I/O should be separable roles so they can scale independently when evidence supports it.

## 4. Manifest-driven provisioning

The central operating model is:

1. Receive a role manifest.
2. Validate its schema, signature/provenance, authorization, version, and policy.
3. Resolve required MicroBundles, service dependencies, hardware capabilities, and resource constraints.
4. Select a host or capacity pool.
5. Materialize only trusted, verified artifacts; preserve BundleId + Version + ContentHash identity.
6. Compose through FSM_COS where composition is required.
7. Start the role through its declared host/runtime adapter.
8. Establish health/readiness and register the instance with routing/discovery.
9. Scale, drain, migrate, or retire it according to explicit policy.

The manifest must be declarative and bounded. It describes an approved role and configuration; it must not be an unrestricted shell script or arbitrary code-loading escape hatch. Artifact retrieval, hash validation, trusted materialization, composition, and execution are separate stages with separate evidence.

The intended experience is that adding a new capability is primarily a matter of supplying a validated manifest and satisfying its declared dependencies—not weeks of bespoke server engineering. The platform still needs reusable role adapters, security policy, compatible artifacts, capacity, and operational controls. Provisioning latency must be benchmarked from request acceptance through readiness.

## 5. Hardware proximity and efficiency hypothesis

The design goal is to place computation as close to the server hardware as practical while preserving portability, isolation, and safety. Manifests and scheduling should expose capability requirements so the fleet can select suitable CPU, memory, storage, network, and accelerator resources without embedding hardware-specific assumptions into Experience semantics.

An early estimate of **30% computational/energy savings** has been proposed for the Workshop technology. This is an unverified hypothesis, not a measured result or commitment. Validation requires representative workloads, a documented baseline, equivalent output/quality, repeatable runs, and separate reporting of compute, memory, network, and energy. Renderer-driven reductions should be measured independently from other optimizations.

## 6. Architectural invariants

- FSM_COS remains the composition kernel. Composition success does not prove a workload is running or healthy.
- FSM_API remains the state/context/process-group primitive; do not invent a second semantic clock.
- ServerApp provisions and operates server roles; it does not absorb every service into one process or own semantics that belong to Experience, Domain, Renderer, or other packages.
- AnyApp, WebApp, and ServerApp are distinct host manifestations with explicit contracts; WebApp must not require a desktop AnyApp process.
- Preserve artifact identity as BundleId + Version + ContentHash. Verify provenance and content before materialization or composition.
- Treat all client input as untrusted. Enforce authentication, authorization, bounded payloads, rate/resource limits, and explicit routing.
- Distinguish requested, accepted, authenticated, authorized, placed, composed, starting, ready, running, draining, completed, failed, and disconnected states where applicable.
- Co-location is a scheduler decision constrained by measured capacity and isolation—not an assumption that all roles should share one server.
- Do not claim persistence, horizontal scaling, migration, automatic provisioning, or energy savings until implemented and tested.

## 7. First proof sequence

The first executable slice should prove the platform's differentiator without prematurely implementing the whole fleet:

1. Define a versioned, validated **server-role manifest** with a narrow initial role and explicit resource/dependency declarations.
2. Build a local provisioning path that validates the manifest, resolves a known role adapter, starts it, and reports readiness/failure.
3. Demonstrate a second instance or role can be provisioned from a separate manifest without bespoke host code.
4. Add resource accounting and a deterministic placement decision before attempting distributed scheduling.
5. Add authenticated service registration and bounded message routing as a separate vertical slice.
6. Only then prove horizontal scale-out and client routing across multiple instances.
7. Add performance/energy benchmarks, including a Renderer delta-delivery comparison, with a documented baseline.

The first role should be selected to minimize external dependencies while proving manifest-to-ready behavior. Do not begin with production authentication, a persistent MMORPG, or a full fleet scheduler as one indivisible milestone.

## 8. First local provisioning slice

A first source implementation now exists under `src/ServerApp.Core` and `src/ServerApp.Cli`. It defines a version-1 server-role manifest, bounded validation, an explicit registered role-adapter interface, and a local provisioning coordinator that reports readiness only when the adapter reports ready. The sample `diagnostic.ready` adapter is in-process and opens no network listener.

A dependency-free contract-check executable is under `tests/ServerApp.ContractChecks`. `ServerApp.FoundryHost` now has a composed path that submits a fixed three-step workflow to Foundry's actual `WorkflowExecutionCoordinator`: validate a host-approved role definition, compose a separate `RuntimeManifest` through FSM_COS, and pass the resulting `RuntimeAssembly` to an `IServerRoleRuntimeAdapter`. The role-to-root mapping is host-owned; the client `ServerRoleManifest` cannot select bundle IDs or handler keys. Foundry's bounded JSON context carries only an opaque operation ID; the live assembly remains in host-owned state. Contract fixtures prove dependency-first composition, composition failure preventing activation, and Ready only after the role adapter confirms readiness. CI pins Foundry, FSM_COS, FSM_API, FSM_UserIO, and MicroBundleDomain source commits and builds in FSM_COS repository-source mode. CI run [#77](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp/actions/runs/41332223051) passed for ServerApp commit `aa65513a04269149ca7cb91266352d98f49e527f`; 23/23 Core contract checks and the Foundry/FSM_COS host contract executable passed. The build emitted 10 warnings from the pinned FSM_API/FSM_COS source projects and zero errors. The fixtures still do not prove trusted repository artifact retrieval/hash verification or production server hosting. The first slice checks declared dependencies against previously Ready instance IDs in the same in-process coordinator. `ProvisioningPlanBuilder` validates closed batches, rejects missing/self/cyclic dependencies, and creates a deterministic topological order. `BatchProvisioningCoordinator` activates that order sequentially, skips dependents after prerequisite failure, and continues independent branches. These remain in-process proofs: they do not reserve hardware, persist instances, schedule across hosts, load arbitrary code, or create a production server. See [Server Role Manifest](SERVER_ROLE_MANIFEST.md).

### 8.1 Deterministic host-capacity placement proof

`HostPlacementPlanner` consumes the validated topological order and a supplied snapshot of host CPU/memory capacity. It uses stable first-fit placement across hosts ordered by ordinal HostId, accounts for already assigned resources, and returns remaining capacity plus any unplaced instances. Invalid host profiles or invalid manifest plans fail closed before placements are emitted.

This is only an in-memory placement calculation. It does not reserve hardware, mutate host records, provision infrastructure, schedule distributed workloads, or account for GPU, network, storage, tenancy isolation, locality, or migration cost. Those signals belong in later measured scheduling contracts.

## 9. Maturity and open engineering questions

Current status: **architecture bootstrap with a source-pinned Foundry → FSM_COS → role-adapter workflow**. Local manifest/placement proofs and the composed workflow are source-implemented with deterministic in-memory fixtures; production server hosting is not. No network server, API, authentication authority, token protocol, trusted repository artifact resolver, database, distributed scheduler, rendering transport, deployment workflow, or production security posture is established yet.

Key questions to resolve through design and small proofs:
- What is the first manifest schema and versioning/compatibility policy?
- Which role adapter is the first proof?
- What is the authoritative identity/session service and token trust model?
- What is the state ownership and handoff model for persistent Experiences?
- Which scheduling signals and SLOs govern placement and scale-out?
- What is the Renderer frame/delta wire format and keyframe/recovery behavior?
- What isolation boundary is required between tenants and co-located roles?
- What are the measured startup, readiness, throughput, latency, and energy targets?

The owner retains control over release, NuGet publication, production deployment, and public exposure. No such authorization is implied by this architecture.

---
<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop avatar" width="200" /></p>
