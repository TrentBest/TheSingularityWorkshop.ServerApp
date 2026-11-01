<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# ServerApp Pause Point

## Current checkpoint — 2026-11-01

- Repository: [TheSingularityWorkshop.ServerApp](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp).
- Active branch: `development`; `master` remains the starting baseline.
- Current stage: local manifest/placement proofs and a source-pinned Foundry → FSM_COS → role-adapter workflow are source-implemented. Candidate-scoped composition sessions now isolate each provisioning operation's FSM_COS composer/catalog boundary and runtime registry. Trusted repository artifact delivery and a production server role host remain incomplete.
- Product direction: manifest-driven provisioning of distinct server roles, including landing hosts, platform services, persistent Workshop domains, authentication and client I/O roles, Experience compute, and rendering.
- Required landing destinations: Social, Settings, Hangouts, Exit, Domains, and Workshop. These must be clearly denoted interactables; visual themes may vary.
- Performance hypotheses: rapid manifest-to-ready activation and an early 30% computational/energy savings estimate are goals/hypotheses, not verified claims.
- No NuGet publication, production deployment, or public endpoint exposure is authorized by this checkpoint.

## Current implementation evidence

- `src/ServerApp.Core`: version-1 server-role manifest, validator, explicit role-adapter contract, local provisioning coordinator.
- `src/ServerApp.Cli`: local CLI with a non-networked `diagnostic.ready` adapter.
- `examples/diagnostic-ready.serverapp.json`: sample manifest.
- `tests/ServerApp.ContractChecks`: dependency-free Core contract checks.
- `src/ServerApp.FoundryHost/FoundryProvisioningHost.cs`: Core-only Foundry workflow fixture; `FoundryComposedProvisioningHost.cs` runs validate → FSM_COS compose → role activation through the actual Foundry coordinator.
- `src/ServerApp.FoundryHost/ServerRoleRuntimeComposition.cs`: host-owned role-to-approved-root catalog, process-local runtime IDs, session creation, and `IServerRoleRuntimeAdapter` contract.
- `src/ServerApp.FoundryHost/ServerRoleCompositionSessions.cs`: per-candidate FSM_COS composer/catalog boundary and runtime registry; successful sessions are retained with the live runtime.
- [Draft PR #3](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp/pull/3): candidate-session implementation; CI run [#41341221563](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp/actions/runs/41341221563) passed, including the concurrent same-BundleId/different-version fixture.
- `tests/ServerApp.FoundryHost.ContractChecks`: checks Foundry lifecycle/cancellation plus FSM_COS dependency-first composition, assembly handoff, composition failure blocking activation, and role readiness gating.
- `.github/workflows/ci.yml`: checks out Foundry, FSM_COS, FSM_API, FSM_UserIO, and MicroBundleDomain at exact commits; builds with `WorkshopUseRepositorySources=true` and runs both contract-check executables.
- Verification status: latest PR CI run #41341221563 passed: Core contract checks 23/23 and Foundry/FSM_COS host contract checks passed against pinned source dependencies.

## Resume sequence

1. Read [Agent Onboarding](../AGENTS.md), [Architecture](ARCHITECTURE.md), and [Founding Fleet Notes](SERVER_FLEET_MODEL.md).
2. Follow the five-repository pinned source checkout instructions in [Foundry Integration](FOUNDRY_INTEGRATION.md) and build/run the composed host contract checks.
3. Keep the passing pinned-source CI path intact; any integration change must retain exact-SHA build and contract-check evidence.
4. Implement candidate-scoped repository-backed artifact resolution: exact immutable address lookup, SHA-256 verification, transitive pinned dependency closure, atomic private-catalog staging, and explicit precompiled materializer allowlisting. The session seam is now in place; artifact retrieval/materialization is not yet implemented.
5. Implement the first real server-role adapter, including an explicit stop/drain/cleanup contract rather than assuming FSM_COS unloads bundles.
6. Add readiness/health contracts and idempotent instance reconciliation.
7. Treat authentication, client gateway, distributed placement, persistent-world handoff, and Renderer delta delivery as separate vertical slices.
8. Benchmark request-to-ready latency and compute/network/energy outcomes before making performance claims.

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
