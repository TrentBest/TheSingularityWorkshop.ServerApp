<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# ServerApp Pause Point

## Current checkpoint — 2026-11-01

- Repository: [TheSingularityWorkshop.ServerApp](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp).
- Active branch: `development`; `master` remains the starting baseline.
- Current stage: founding fleet architecture captured; local manifest/placement proofs and a source-pinned Foundry workflow host integration are source-implemented. FSM_COS composition and production role hosting remain incomplete.
- Product direction: manifest-driven provisioning of distinct server roles, including landing hosts, platform services, persistent Workshop domains, authentication and client I/O roles, Experience compute, and rendering.
- Required landing destinations: Social, Settings, Hangouts, Exit, Domains, and Workshop. These must be clearly denoted interactables; visual themes may vary.
- Performance hypotheses: rapid manifest-to-ready activation and an early 30% computational/energy savings estimate are goals/hypotheses, not verified claims.
- No NuGet publication, production deployment, or public endpoint exposure is authorized by this checkpoint.

## Current implementation evidence

- `src/ServerApp.Core`: version-1 server-role manifest, validator, explicit role-adapter contract, local provisioning coordinator.
- `src/ServerApp.Cli`: local CLI with a non-networked `diagnostic.ready` adapter.
- `examples/diagnostic-ready.serverapp.json`: sample manifest.
- `tests/ServerApp.ContractChecks`: dependency-free Core contract checks.
- `src/ServerApp.FoundryHost`: submits provisioning workflows to Foundry's real `WorkflowExecutionCoordinator` using an explicitly registered handler.
- `tests/ServerApp.FoundryHost.ContractChecks`: verifies ready, not-ready failure, duplicate pending submission rejection, and accepted-run cancellation.
- `.github/workflows/ci.yml`: checks out Foundry at exact commit `8bd3487a96af2cd7d667f62c4566a499bafe30dc`, builds the host integration, and runs both contract-check executables.
- Verification status: source and CI workflow updated; the exact latest workflow result still needs confirmation. Do not call this CI-passed yet.

## Resume sequence

1. Read [Agent Onboarding](../AGENTS.md), [Architecture](ARCHITECTURE.md), and [Founding Fleet Notes](SERVER_FLEET_MODEL.md).
2. Clone Foundry into `_deps/Foundry` at the exact pinned commit recorded in [Foundry Integration](FOUNDRY_INTEGRATION.md), then build/run the host integration contract checks.
3. Verify the exact ServerApp CI run; fix compiler/test failures before expanding the integration.
4. Implement the separate FSM_COS composition handoff and a real RuntimeAssembly-consuming role host; do not equate workflow success with runtime readiness.
4. Add readiness/health contracts and idempotent instance reconciliation.
5. Treat authentication, client gateway, distributed placement, persistent-world handoff, and Renderer delta delivery as separate vertical slices.
6. Benchmark request-to-ready latency and compute/network/energy outcomes before making performance claims.

## Known constraints

- FSM_COS owns composition; FSM_API provides state/context/process-group primitives.
- AnyApp and WebApp are independent host manifestations.
- ServerApp provisions and operates server roles; it does not duplicate FSM_COS, invent a second semantic clock, or collapse all roles into one backend.
- Validate manifests, artifact provenance, authorization, resource limits, and all client input.
- Preserve BundleId + Version + ContentHash identity and separate retrieval, verification, materialization, composition, and execution evidence.
- The owner controls branch deletion, release operations, NuGet publication, and production deployment.

---
<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop avatar" width="200" /></p>
