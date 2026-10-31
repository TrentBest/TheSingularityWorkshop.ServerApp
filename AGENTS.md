<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Repository Instructions for AI Agents

## Mission

You are helping establish ServerApp. Treat this repository as an active product/domain, not as a generic ASP.NET template. Read this file and the linked architecture/documentation before changing boundaries. The owner is available to resolve product decisions; do not turn unknown requirements into silently assumed contracts.

## Branch policy — mandatory

The permanent branch model is exactly:

- `development` — authoritative source for active work and the next candidate.
- `master` — released/stable baseline until the owner authorizes its replacement.

Work directly on `development`. Do not create persistent feature, experiment, release-prep, or handoff branches. The owner performs branch deletion and any release-branch replacement. Do not delete or rewrite branches on the owner's behalf.

## Integration and evidence

- Preserve useful existing work; inspect source, tests, docs, workflows, and history before replacing anything.
- Keep implementation, tests, and documentation aligned.
- Distinguish implemented, source-checked, verified, conceptual, and planned behavior.
- Do not claim a build, test, deployment, runtime behavior, or security property without evidence tied to the relevant revision.
- Record unresolved architecture decisions explicitly and ask the owner when a choice changes product boundaries.

## Architecture boundaries

Use the neighboring Workshop hosts as references, not as permission to copy their presentation or runtime implementation:

- [FSM_COS](https://github.com/TrentBest/TheSingularityWorkshop.FSM_COS) owns runtime composition: it resolves and validates a host-supplied MicroBundle catalog, composes dependency order, performs bounded arbitration, and returns a `RuntimeAssembly`. The host owns application execution, lifecycle, UI, and presentation.
- [AnyApp](https://github.com/TrentBest/AnyApp) is the desktop host manifestation.
- [WebApp](https://github.com/TrentBest/TheSingularityWorkshop.WebApp) is the browser-facing host manifestation and must remain independently usable without desktop AnyApp.
- ServerApp's exact server-host responsibilities must be established in [Architecture](docs/ARCHITECTURE.md) before implementation hardens them into contracts.

Do not make ServerApp a second FSM_COS, invent a competing Experience lifecycle, or assume WebApp must proxy through a desktop process. Keep server-only concerns separate from shared application semantics and presentation.

## Dependency and release constraints

- Prefer source/development integration while dependencies are under active development; do not pin unpublished package versions merely to imitate a future release graph.
- Do not publish NuGet packages, deploy production services, or expose public endpoints unless the owner explicitly authorizes that specific action.
- Do not add dependencies solely because a package exists.
- Never commit secrets, credentials, local environment files, or production connection strings.

## Before starting a task

1. Read [Architecture](docs/ARCHITECTURE.md) and [Documentation Index](DOCUMENTATION_INDEX.md).
2. Inspect current `development` and `master` heads and relevant CI state.
3. Identify the smallest implementation slice and its verification evidence.
4. Update theory, contracts, tests, and status documentation with the implementation.
5. Ask the owner when the next step requires a product decision not established by source or prior direction.

---
<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop avatar" width="200" /></p>
