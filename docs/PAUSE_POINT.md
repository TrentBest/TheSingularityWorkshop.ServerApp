<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# ServerApp Pause Point

## Current checkpoint — 2026-10-31

- Repository: [TheSingularityWorkshop.ServerApp](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp).
- Active branch: `development`; `master` remains the starting baseline.
- Current stage: documentation and architecture bootstrap. No runnable server or endpoint has been established.
- Initial architecture question: independent headless Experience host, supporting server services, or a deliberately separated combination. See [Architecture](ARCHITECTURE.md).
- No NuGet publication, deployment, or public endpoint exposure is authorized by this checkpoint.

## Resume sequence

1. Read [Agent Onboarding](../AGENTS.md), [Architecture](ARCHITECTURE.md), and [Documentation Index](../DOCUMENTATION_INDEX.md).
2. Confirm the server's product role with the owner.
3. Select one narrow vertical slice and define its trust boundary and runtime owner.
4. Implement tests and source-backed documentation together.
5. Record build/test evidence against the exact commit before claiming the slice is verified.

## Known constraints

- FSM_COS owns composition, not application execution or presentation.
- AnyApp and WebApp are sibling host manifestations, not a default client/server dependency pair.
- Do not introduce an independent semantic tick loop or claim that composition alone means an Experience is running.
- Keep server responsibilities explicit; do not invent authentication, persistence, multi-tenancy, deployment, or remote code-loading guarantees before requirements and tests exist.
- The owner controls branch deletion, release operations, NuGet publication, and production deployment.

---
<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop avatar" width="200" /></p>
