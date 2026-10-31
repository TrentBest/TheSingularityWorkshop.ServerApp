<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# ✳️ 00 ServerApp

**ServerApp is the Workshop's server-side host initiative.** It is being established alongside [AnyApp](https://github.com/TrentBest/AnyApp) and [WebApp](https://github.com/TrentBest/TheSingularityWorkshop.WebApp), with [FSM_COS](https://github.com/TrentBest/TheSingularityWorkshop.FSM_COS) providing the composition boundary where appropriate.

This repository is in its birth stage. The current source does not yet establish a runnable server, API contract, persistence model, deployment topology, or production security posture. This README intentionally describes the architectural investigation rather than claiming those capabilities exist.

This document follows the Workshop's [Documentation Standard](https://github.com/TrentBest/TheSingularityWorkshop.FSM_COS/blob/master/DOCUMENTATION_STANDARD.md). See [Agent Onboarding](AGENTS.md) before contributing.

## 🟦 01 — What is ServerApp?

ServerApp is intended to provide a server-side manifestation of the Workshop's application architecture. Its exact responsibility boundary is being established: it must be clear whether it is an independently hosted server application that composes/executes Experiences, a set of server services consumed by other hosts, or a deliberately separated combination of those roles.

That decision matters. A server host, an API gateway, an artifact repository, and a remote execution service are different products with different trust, lifecycle, and operational responsibilities. They should not be collapsed into one generic "backend" without an explicit contract.

## 🟣 02 — Why establish it separately?

The desktop and browser hosts have different native capabilities and presentation surfaces. A server manifestation has a different set of concerns: network trust boundaries, authenticated requests, service lifecycle, concurrency, durable state, observability, and deployment. Those concerns should be designed as server responsibilities rather than leaking into shared Experience semantics or FSM_COS composition.

## 🩵 03 — Architectural boundaries

| System | Responsibility |
|---|---|
| FSM_COS | Compose a requested runtime from a host-supplied catalog; return a `RuntimeAssembly`. |
| AnyApp | Desktop host and desktop presentation. |
| WebApp | Browser-facing host, browser capabilities, and web presentation. |
| ServerApp | Server-side host/service boundary — exact scope is an open design decision. |

ServerApp must not duplicate FSM_COS composition, invent an independent semantic tick/lifecycle, or become a required relay through which WebApp must pass. Any remote protocol must be explicit, versioned, authenticated, bounded, and independently testable.

See [Architecture and Open Decisions](docs/ARCHITECTURE.md) for the current theory and the decision that must be resolved before choosing the first vertical slice.

## 🟢 04 — See it in a minute

There is not yet a runnable application or verified server endpoint in this repository. The first executable proof will be documented here only after its project, startup path, tests, and local run instructions exist.

## 🟪 05 — Documentation and next steps

- [Architecture and Open Decisions](docs/ARCHITECTURE.md) — theory, boundaries, unresolved choices, and a proposed proof sequence.
- [Documentation Index](DOCUMENTATION_INDEX.md) — repository documentation map.
- [Agent Onboarding](AGENTS.md) — branch, architecture, evidence, and contribution rules.

The initial milestone is to establish the correct server role and prove one small end-to-end path before expanding APIs, persistence, remote execution, or deployment infrastructure.


---
<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop avatar" width="200" /></p>
