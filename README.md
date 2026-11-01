<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# ✳️ 00 ServerApp

**ServerApp is the Workshop's manifest-driven server fleet and host platform.** Its direction is to provision distinct server roles from validated manifests, place them on suitable hardware, and scale them according to measured demand.

This repository is in its architecture and bootstrap stage. The fleet model is captured as founding product direction; it is not yet an implemented server platform. No runnable server, public endpoint, authentication service, scheduler, persistence system, or deployment workflow is claimed here.

This document follows the Workshop's [Documentation Standard](https://github.com/TrentBest/TheSingularityWorkshop.FSM_COS/blob/master/DOCUMENTATION_STANDARD.md). See [Agent Onboarding](AGENTS.md) before contributing.

## 🟦 01 — What is ServerApp?

ServerApp is intended to make server capabilities declarative and repeatable. A validated manifest describes a server role, dependencies, configuration, and resource requirements. ServerApp should resolve trusted artifacts, select capacity, activate the role, and report readiness.

The long-term target is to bring a required server role online as quickly as practical without bespoke engineering for every deployment. Startup and readiness latency must be measured; "instantaneous" is an engineering goal, not a current guarantee.

## 🟣 02 — Why a fleet of distinct roles?

The Workshop vision includes landing-experience hosts, settings/social/hangouts/chat services, persistent Workshop domains and Singularity Central Station, domain facades, login and authentication services, client I/O gateways, Experience compute hosts, and rendering servers.

These roles have different security, state, scaling, and resource requirements. They may be co-located when safe and scaled independently when necessary. ServerApp should not turn them into one undifferentiated backend.

## 🩵 03 — Architectural boundaries

| System | Responsibility |
|---|---|
| FSM_COS | Runtime composition from a host-supplied catalog; returns RuntimeAssembly to its host. |
| FSM_API | State/context and process-group execution primitives. |
| AnyApp | Desktop host and presentation. |
| WebApp | Browser-facing host and presentation. |
| ServerApp | Server-role provisioning, hosting, fleet operations, and bounded routing. |
| Foundry | Owns provisioning workflow execution and lifecycle; the composed ServerApp workflow runs FSM_COS composition and role activation as registered steps. |
| Renderer | Visual output representation and potential reduction of server computation/traffic. |
| Domains | External identity/facade and policy for Experiences. |
| Experiences | Interactive content and runtime state. |

A landing Experience must expose interactable transfer points for Social, Settings, Hangouts, Exit, Domains, and Workshop. Its visual theme may vary—from a garden to a maze—but the required destinations must remain clearly denoted and operable.

See [Architecture](docs/ARCHITECTURE.md) and [Founding Fleet Notes](docs/SERVER_FLEET_MODEL.md) for the full model.

## 🟢 04 — Fleet Observatory

Enter the interactive [ServerApp Fleet Lab](https://trentbest.github.io/TheSingularityWorkshop.ServerApp/). Experiment with host counts, CPU and memory budgets, workload profiles, and first-fit placement. The browser simulation is illustrative; the linked source and CI runs provide the implementation evidence. It does not connect to or provision real hosts.

## 🟢 05 — See it in a minute

The local manifest-to-ready proof and the first Foundry workflow host integration are source-implemented. With the .NET 8 SDK, run the diagnostic Core checks:

```bash
dotnet run --project src/ServerApp.Cli/ServerApp.Cli.csproj -- examples/diagnostic-ready.serverapp.json
dotnet run --project tests/ServerApp.ContractChecks/ServerApp.ContractChecks.csproj
```

To build and run the composed Foundry/FSM_COS integration checks, first place the five source repositories at the exact revisions used by CI:

```bash
git clone https://github.com/TrentBest/TheSingularityWorkshop.Foundry.git _deps/Foundry
git -C _deps/Foundry checkout 8bd3487a96af2cd7d667f62c4566a499bafe30dc
git clone https://github.com/TrentBest/TheSingularityWorkshop.FSM_COS.git _deps/FSM_COS
git -C _deps/FSM_COS checkout cad5775fe55a6a8d0b6df8bbd22017c1179141c4
git clone https://github.com/TrentBest/FSM_API.git _deps/FSM_API
git -C _deps/FSM_API checkout 4db572f5e0e74df028b01ee22fb393e2bd203eb6
git clone https://github.com/TrentBest/FSM_UserIO.git _deps/FSM_UserIO
git -C _deps/FSM_UserIO checkout db5b59d0521a341f97e6077e3508eb2bd7171607
git clone https://github.com/TrentBest/TheSingularityWorkshop.MicroBundleDomain.git _deps/TheSingularityWorkshop.MicroBundleDomain
git -C _deps/TheSingularityWorkshop.MicroBundleDomain checkout d8c1f52d3550591aec2337ad4470bfadb20fb11e
dotnet run --project tests/ServerApp.FoundryHost.ContractChecks/ServerApp.FoundryHost.ContractChecks.csproj --configuration Release -p:WorkshopUseRepositorySources=true
```

The diagnostic adapter and composed role adapters are in-process fixtures; the composed contract checks exercise actual FSM_COS composition and Foundry workflow execution, but they open no network port and do not prove repository-backed artifact verification or production/distributed hosting. See [Server Role Manifest](docs/SERVER_ROLE_MANIFEST.md) and [Foundry Integration](docs/FOUNDRY_INTEGRATION.md) for boundaries and limitations.

## 🟪 06 — Documentation and next steps

- [Architecture](docs/ARCHITECTURE.md) — system boundaries, server roles, provisioning model, and proof sequence.
- [Foundry Integration Boundary](docs/FOUNDRY_INTEGRATION.md) — selected execution ownership, actual source contracts, and the first integration proof.
- [Founding Fleet Notes](docs/SERVER_FLEET_MODEL.md) — captured product vision and role inventory.
- [Server Role Manifest](docs/SERVER_ROLE_MANIFEST.md) — first manifest contract and local provisioning proof.
- [Documentation Index](DOCUMENTATION_INDEX.md) — repository documentation map.
- [Agent Onboarding](AGENTS.md) — branch, architecture, evidence, and contribution rules.

The first milestone is a validated role manifest and one deterministic local provisioning path. Authentication, distributed scheduling, persistent worlds, rendering transport, and production deployment should be separate milestones with independently verified contracts.

---
<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop avatar" width="200" /></p>
