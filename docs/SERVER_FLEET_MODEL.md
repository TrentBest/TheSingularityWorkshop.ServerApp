<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Server Fleet Model — Founding Notes

## Why this document exists

These notes capture the initial product vision for ServerApp as a fleet of manifest-provisioned server roles. They are architectural direction, not a claim that any listed server role has been implemented.

## Player journey and landing experiences

A player enters through a landing Experience. The default visual theme may be a garden. Other themes are allowed: a maze could require exploration to discover gates or portals. The theme may change the journey, but a landing Experience qualifies only when it clearly denotes and makes interactable the required destinations:

- Social
- Settings
- Hangouts
- Exit
- Domains
- Workshop

These destinations are transfer points to the next Experience or platform context. The required semantic destinations must remain discoverable and operable regardless of visual design. The eventual contract should distinguish a destination's declared presence, its discoverability/accessibility, and a successful transition.

## Distinct server roles envisioned

| Role | Responsibility in the vision |
|---|---|
| Landing-experience host | Hosts one or more landing Experiences and their transfer points. |
| Settings server | Owns settings-related service operations. |
| Social server | Owns social interactions and related state. |
| Hangouts server | Hosts hangout-oriented experiences/services. |
| Workshop server domains | Hosts the persistent, entangled Workshop platform domains. |
| Singularity Central Station | Persistent hub presenting platforms as trains to their respective domains. |
| Domain facade | Exposes the creator-selected external facade for an Experience, constrained by domain policy. |
| Login/broker server | Receives login requests and forwards them to the authentication authority. |
| Authentication server | Validates credentials/policy and issues or authorizes scoped client tokens. |
| Client I/O gateway | Direct client communication endpoint; processes and routes bounded messages to the responsible service/Experience. |
| Experience compute host | Runs one or more Experience workloads when resource profiles and isolation permit. |
| Rendering server | Produces frames and sends encoded deltas when smaller than full-frame delivery. |
| Chat server | Provides chat as a separable service role. |
| Private-business server | Provides isolated/private business-oriented server capabilities. |

Roles are independently deployable and independently scalable. They may be co-located when resource, security, and availability constraints permit. Small dedicated services are intended to support low-cost monthly rental tiers; actual pricing and operating costs require measurement.

## Persistent universes and elastic compute

Persistent worlds may be distributed across a fleet. Provisioning should respond to demand: add compute capacity when player density, simulation cost, or latency requires it; redistribute work while preserving world authority and state consistency. A host may serve many Experiences only when measured profiles fit. More players may require additional instances even within one Experience.

The eventual scheduler must account for spatial hotspots, interaction locality, state ownership, migration/handoff cost, network latency, and hardware capability—not just the number of connected players.

## Manifest as the provisioning unit

The intended workflow is to provide ServerApp with a validated manifest describing a server role, its dependencies, configuration, and resource needs. ServerApp resolves trusted artifacts, chooses suitable capacity, starts the role, and reports readiness. The goal is to avoid bespoke engineering for every new server role.

A manifest is declarative configuration, not an arbitrary script. Security, compatibility, artifact integrity, resource limits, health checks, and rollback/drain behavior are part of the platform contract.

"Instantaneous" startup is an aspiration. Measure request-to-accepted, placement, dependency resolution, artifact materialization, startup, and ready-to-serve latency separately. Warm capacity may make some activations very fast, but must be accounted for explicitly.

## Renderer as a compute multiplier

The Renderer may reduce server-side computation and network traffic by generating frames, comparing successive frames, and sending a delta only when its encoded size (including protocol overhead) is smaller than a full frame. The protocol needs keyframes, recovery, ordering, and decode-cost rules. Benchmark it against full-frame delivery and equivalent visual output.

The early estimate of 30% computational/energy savings is a hypothesis only. No savings claim should be published until representative, repeatable measurements establish the baseline and isolate the contribution of each optimization.

## Principles to preserve

1. Separate Experience semantics, Domain facade/policy, server role, and manifest.
2. Keep FSM_COS as composition kernel and FSM_API as execution primitives.
3. Keep client I/O, authentication authority, and Experience compute as separate trust/scaling concerns.
4. Make placement and co-location evidence-driven.
5. Treat every client request and manifest as untrusted until validated and authorized.
6. Make provisioning measurable, reproducible, and reversible.
7. Do not equate a running process with a healthy, ready, or correctly routed service.

## Status

Founding product notes, captured 2026-11-01. All server roles and fleet capabilities above remain conceptual until implemented, tested, and documented against a specific revision.

---
<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop avatar" width="200" /></p>
