<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# ✳️ 00 ServerApp

[![Sith Policy](assets/sith-policy.svg)](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp/actions/workflows/sith-policy.yml)
**ServerApp is the Workshop's manifest-driven server fleet and host platform.** Its direction is to provision distinct server roles from validated manifests, place them on suitable hardware, and scale them according to measured demand.

This repository is in its architecture and bootstrap stage. The fleet model is captured as founding product direction; it is not yet a production server platform. Source-backed local provisioning, host workflow composition, a durable bot-build registry, and a game-neutral match-session lifecycle coordinator are implemented as bounded slices. No public endpoint, authentication service, distributed scheduler, or production deployment workflow is claimed here.

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

## 🟢 04 — Fleet and Competitive Ladder Labs

Enter the interactive [ServerApp Fleet Lab](https://trentbest.github.io/TheSingularityWorkshop.ServerApp/). Experiment with host counts, CPU and memory budgets, workload profiles, and first-fit placement. The browser simulation is illustrative; it does not connect to or provision real hosts.

The [Competitive Ladder Lab](https://trentbest.github.io/TheSingularityWorkshop.ServerApp/competitive-ladder/) demonstrates the StarCraft II competition model: formats, creator-owned bot identities, immutable builds, and 1v1 admission. It is a static simulation, not a live ladder or game-session host. The current source-backed implementation and CI evidence are documented in [Competitive Ladder Composition](docs/COMPETITIVE_LADDER_COMPOSITION.md).

## 🟦 Open the repository in Visual Studio

Open `ServerApp.sln` from the repository root in Visual Studio Community 2026. The default solution includes Core, CLI, MachineReport, and dependency-free Core ContractChecks. The optional DataWarehouse adapter and its integration checks are built separately when that capability is needed.

The default ServerApp solution and core contract checks do not require DataWarehouse or the file store. The fixed-record DataWarehouse catalogue and durable file store are isolated integrations with dedicated contract-check executables. CI checks them explicitly, without making either a dependency of every ServerApp consumer.

To build the dependency-free core:

```powershell
dotnet build .\ServerApp.sln --configuration Release
dotnet run --project tests/ServerApp.ContractChecks/ServerApp.ContractChecks.csproj --configuration Release
```

To build and exercise the database-free file store (it has no external dependencies), run:

```powershell
dotnet build .\src\ServerApp.FileStore\ServerApp.FileStore.csproj --configuration Release
dotnet run --project tests/ServerApp.FileStore.ContractChecks/ServerApp.FileStore.ContractChecks.csproj --configuration Release
```

The file store is intentionally outside the default solution. It is an opt-in host-storage adapter; the host opens it through `IBotBuildRegistryStore` and supplies it to `DurableBotBuildRegistry.OpenAsync`.

To opt into the DataWarehouse integration, check out its pinned source and run its dedicated build and checks:

```powershell
git clone https://github.com/TrentBest/TheSingularityWorkshop.DataWarehouse.git _deps/TheSingularityWorkshop.DataWarehouse
git -C _deps/TheSingularityWorkshop.DataWarehouse checkout a019d006dbb2eb4cb525cd3977c9eafaf6eee616
dotnet build .\src\ServerApp.DataWarehouse\ServerApp.DataWarehouse.csproj --configuration Release
dotnet run --project .\tests\ServerApp.DataWarehouse.ContractChecks\ServerApp.DataWarehouse.ContractChecks.csproj --configuration Release
```

`src/ServerApp.Core/GameSessionContracts.cs` defines the game-neutral session adapter boundary and coordinator. It requires the injected adapter to establish Ready before a match session is reported ready, pins the adapter version from the immutable match assignment, and validates subsequent lifecycle transitions. The actual StarCraft II session MicroBundle belongs in the existing [Experiences repository](https://github.com/TrentBest/TheSingularityWorkshop.Experiences); no separate SC2 repository is needed.

The bot-build registry now has a concrete database-free file store in `src/ServerApp.FileStore`. It appends checksummed binary revision frames containing only changed registrations/removals, arbitrates writers across processes, performs revision compare-and-swap, and flushes before acknowledging a commit. Startup validates a compact checkpoint and replays the journal; an incomplete final frame is treated as a torn tail, while complete corruption and unknown schema versions fail closed. `CompactAsync` installs a full checkpoint before rotating the journal. The DataWarehouse coordinator still commits durable state before publishing its replacement hot catalogue. The store is intentionally outside the default solution and is built/tested explicitly by CI, preserving dependency-free ServerApp Core consumers. Its disk-recovery contract checks are in `tests/ServerApp.FileStore.ContractChecks`; CI execution is the next verification gate, so source implementation must not yet be described as a passing durability proof. See [Competitive Ladder Composition](docs/COMPETITIVE_LADDER_COMPOSITION.md) and issue [#9](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp/issues/9).

The Foundry host integration and its contract checks remain source-integrated projects that additionally require the other pinned repositories under `_deps`; CI checks those separately.

## 🟢 05 — Inspect a deployment machine

The dependency-free `ServerApp.MachineReport` utility gathers OS/build architecture, CPU, physical memory, graphics adapter, installed .NET SDKs/runtimes, storage capacity, and active network interfaces. It copies the report to the clipboard when a supported clipboard utility is available. Review the output before sharing: it includes the computer name and private LAN IPv4 addresses, but does not collect usernames, credentials, serial numbers, MAC addresses, file listings, or public-IP lookups.

```bash
dotnet run --project src/ServerApp.MachineReport/ServerApp.MachineReport.csproj --configuration Release
```

Run this on the intended ServerApp host machine, then paste the copied report into the conversation. The utility only reads system information; it does not install software or change machine settings.

## 🟢 06 — See it in a minute

The local manifest-to-ready proof and the first Foundry workflow host integration are source-implemented. With the .NET 8 SDK, run the diagnostic Core checks:

```bash
dotnet run --project src/ServerApp.Cli/ServerApp.Cli.csproj -- examples/diagnostic-ready.serverapp.json
dotnet run --project tests/ServerApp.ContractChecks/ServerApp.ContractChecks.csproj
```

To build and run the composed Foundry/FSM_COS integration checks, first place the other five source repositories at the exact revisions used by CI (DataWarehouse is listed above):

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

## 🟪 07 — Documentation and next steps

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
