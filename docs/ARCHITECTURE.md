<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# ServerApp Architecture and Open Decisions

## 1. Purpose

ServerApp is the Workshop's server-side host initiative. Its purpose is not yet sufficiently specified to justify a concrete API, service topology, persistence layer, or remote execution protocol. This document establishes the known architectural constraints and makes the missing product decision visible before implementation hardens an assumption.

The guiding rule is: **server concerns belong in the server host; shared application semantics and composition remain in their owning domains.**

## 2. Neighboring systems and ownership

| System | Established responsibility | ServerApp implication |
|---|---|---|
| FSM_COS | Resolves and validates a host-supplied MicroBundle catalog, composes dependencies, performs bounded arbitration, and returns a RuntimeAssembly. | Consume its composition contract where server-host composition is required; do not reimplement it. |
| AnyApp | Windows desktop host; owns desktop input and presentation around the Workshop runtime. | Reuse host-neutral contracts where appropriate; do not depend on the WPF executable as a server library. |
| WebApp | Browser-facing host; owns browser input, browser capabilities, and web presentation. It is not a desktop proxy. | Treat WebApp as a sibling host/client boundary, not automatically as a backend that ServerApp must serve. |
| FSM_API | State/context and process-group execution primitives. | Do not invent a competing semantic lifecycle or heartbeat. Identify the canonical execution owner before implementing remote progress. |
| MicroBundleDomain / repository contracts | Define MicroBundle identity/contracts and artifact/catalog responsibilities in their respective domains. | Preserve immutable identity and trust boundaries; do not turn ServerApp into an artifact loader without an explicit materialization contract. |

## 3. Candidate role models — not yet selected

These are mutually distinguishable product shapes, not implemented features:

### A. Server as an independent host

ServerApp is a headless host manifestation. It accepts an explicit application/Experience request, validates the request and artifact identity, supplies the appropriate catalog/configuration to FSM_COS, and then delegates execution to the canonical runtime owner. It exposes bounded results and status over an authenticated protocol. It must define session ownership, resource limits, cancellation, concurrency, and isolation.

### B. Server as supporting services

ServerApp provides server-owned capabilities such as identity/session services, data access, artifact discovery, or coordination to other hosts. It does not itself become the owner of Experience execution. Each service has an explicit API, data owner, authorization policy, and operational lifecycle.

### C. Deliberately separated host and service layers

A server host and supporting services coexist only where the boundaries are independently useful and testable. They must not become a single undifferentiated backend. The service interfaces, trust boundaries, and deployment units remain explicit.

No option is selected by this document. The owner must confirm the intended role before the first production-facing contract is established.

## 4. Non-negotiable boundaries

- FSM_COS remains the composition kernel; a successful composition result is not proof that an Experience is executing.
- The host supplies its catalog and owns application execution, lifecycle, delivery, and presentation unless a separately documented runtime owner says otherwise.
- ServerApp must not create a second FSM_COS or an independent semantic clock.
- WebApp and AnyApp remain independently meaningful host manifestations; do not require a desktop AnyApp process for ordinary web/server operation.
- Browser-originated requests are untrusted input. Never expose arbitrary shell/process execution, arbitrary filesystem access, unrestricted assembly loading, or unrestricted local-network access as a convenience API.
- Preserve artifact identity as BundleId + Version + ContentHash where published MicroBundle artifacts are involved. Retrieval, hash validation, trusted materialization, and composition are separate stages and must be evidenced separately.
- Distinguish requested, accepted, authorized, composed, running, completed, failed, and disconnected states. An HTTP success or composition success must not be mislabeled as runtime success.

## 5. First proof sequence after role selection

1. Write the server's one-sentence product purpose and explicit non-goals.
2. Define one request/response contract with versioning, authentication, authorization, validation, and bounded payload/resource limits.
3. Implement one vertical slice with deterministic tests and observable failure paths.
4. Prove the selected runtime/composition boundary against the actual current source APIs; do not infer it from diagrams.
5. Add operational health/readiness, structured diagnostics, and cancellation only where the first slice needs them.
6. Document local execution and the evidence/revision for every claimed capability.

Deployment topology, durable storage, multi-tenancy, remote MicroBundle materialization, and production exposure remain out of scope until a concrete requirement selects them.

## 6. Verification and maturity

Current status: **conceptual / repository bootstrap**. No runnable server, endpoint, authentication scheme, database, deployment workflow, or production security claim is established by the repository's initial source. Each future claim must be classified as conceptual, source-checked, or verified against a named revision and test/run path.

## 7. Open decision for the owner

Which is the intended first identity of ServerApp?

- an independent headless host that can compose and run a server-side Experience;
- server-side services that WebApp/AnyApp consume, without owning Experience execution; or
- a deliberately separated combination of both?

This decision determines the first API, runtime owner, session model, and security architecture. Do not choose implicitly.


---
<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop avatar" width="200" /></p>
