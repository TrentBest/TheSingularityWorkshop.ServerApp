<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# ServerApp Branch Reconciliation Ledger

## Canonical branch model

Exactly two permanent branches are intended:

- `development` — active integration and next-candidate source of truth.
- `master` — stable/released baseline until the owner authorizes replacement.

Work directly on `development`. Do not create additional persistent branches. Do not delete or rewrite branches; the owner performs branch cleanup and release operations.

## Initial bootstrap — 2026-10-31

| Branch | Initial disposition |
|---|---|
| `master` | Preserved as repository starting baseline. |
| `development` | Created from `master` and selected for active work. |

The initial development commits establish repository onboarding, a documentation index, and architecture boundaries/open decisions. No application source or runtime behavior is claimed by this bootstrap.

## Reconciliation procedure

For any future noncanonical branch, compare its commits and file changes against `development`; inspect source, tests, documentation, workflows, and PR history; port useful work selectively; verify the result; and tell the owner which exact branch is safe to delete. Never delete it for the owner.

## Release boundary

A branch update or replacement is not authorization to publish NuGet packages or deploy a service. Require explicit owner authorization for the exact publication or deployment action.

---
<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop avatar" width="200" /></p>
