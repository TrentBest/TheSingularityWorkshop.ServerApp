<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# ServerApp Pause Point

## Current checkpoint — 2026-11-01

- Repository: [TheSingularityWorkshop.ServerApp](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp).
- Active branch: `development`; `master` remains the starting baseline.
- Current stage: founding fleet architecture captured; no runnable server or endpoint has been established.
- Product direction: manifest-driven provisioning of distinct server roles, including landing hosts, platform services, persistent Workshop domains, authentication and client I/O roles, Experience compute, and rendering.
- Required landing destinations: Social, Settings, Hangouts, Exit, Domains, and Workshop. These must be clearly denoted interactables; visual themes may vary.
- Performance hypotheses: rapid manifest-to-ready activation and an early 30% computational/energy savings estimate are goals/hypotheses, not verified claims.
- No NuGet publication, production deployment, or public endpoint exposure is authorized by this checkpoint.

## Resume sequence

1. Read [Agent Onboarding](../AGENTS.md), [Architecture](ARCHITECTURE.md), and [Founding Fleet Notes](SERVER_FLEET_MODEL.md).
2. Confirm the first server-role manifest shape and choose one narrow role for the initial proof.
3. Implement manifest validation and a deterministic local manifest-to-ready provisioning path.
4. Add tests, resource accounting, health/readiness, and evidence against the exact commit.
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
