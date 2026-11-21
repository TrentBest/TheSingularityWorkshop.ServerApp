# Autonomous Swarm Simulation

## Status

The first deterministic virtual coordination slice and its first fault-injection harness are implemented on `development`. Both use the existing `ServerApp.Core.SimulationMessageFabric`; neither introduces a second queue or execution authority.

## Normal scenario

`ServerApp.Simulation.AutonomousSwarmSimulation.RunAsync()` creates a bounded world of virtual survey agents:

- Up to 8 sub-swarms, with up to 8 agents per sub-swarm
- One host-created coordinator, one participant per sub-swarm, and one participant per agent
- A finite message vocabulary: group assignment, benign task assignment, and acknowledgement
- Three virtual delivery steps: coordinator to sub-swarm, sub-swarm to agents, agents to coordinator
- Stable message IDs and deterministic ordering
- A fixed per-agent resource budget; one unit is consumed per assigned survey task
- Immutable result snapshots and a fabric trace

The default scenario contains 3 sub-swarms and 12 agents. Each agent receives one harmless virtual survey-cell task and acknowledges it. The run checks that all assignments are acknowledged by the coordinator and that resource accounting is conserved. A violated invariant throws rather than returning a false success.

## Fault-injection scenario

`ServerApp.Simulation.SwarmFaultInjectionSimulation.RunAsync(mode)` runs one bounded virtual task under a selected deterministic fault:

| Fault mode | Expected outcome | Evidence |
|---|---|---|
| `ParticipantHandlerFailure` | `Failed` | Fabric records a failed handler; no coordinator acknowledgement |
| `DelayedTaskExpiry` | `Incomplete` | Task reaches its expiry step before delivery; no acknowledgement |
| `DuplicateTaskAttempt` | `Completed` | Same message ID and content is recorded as duplicate; one delivery and one acknowledgement |

The result reports a terminal outcome plus accepted, duplicate, delivered, expired, and failed task-message counts, coordinator acknowledgement count, and the immutable fabric trace. The duplicate case demonstrates idempotent enqueue suppression, not exactly-once processing. The failure and expiry cases do not retry, fabricate acknowledgements, or claim completion.

These are deliberately narrow deterministic fault proofs. They do not yet provide automatic recovery, retry policy, participant restart, partial-world continuation, or a general-purpose failure scheduler.

## Full-hierarchy fault injection

`ServerApp.Simulation.AutonomousSwarmFaultSimulation.RunAsync(mode, faultedAgentId)` applies one deterministic fault inside the same bounded 3-group/12-agent message hierarchy. The default target is `group-02-agent-02`; only one of the twelve registered agent IDs is accepted.

| Fault mode | Full-swarm outcome | Coordinator evidence |
|---|---|---|
| `ParticipantHandlerFailure` | `Failed` | 11 of 12 agents acknowledge; the target participant handler is recorded as failed |
| `DelayedTaskExpiry` | `Incomplete` | 11 of 12 agents acknowledge; the target task expires before delivery |
| `DuplicateTaskAttempt` | `Completed` | All 12 agents acknowledge; the identical duplicate is suppressed before a second delivery |

The immutable result includes every agent's terminal state, remaining resource units, coordinator acknowledgement, the complete fabric trace, and aggregate assignment/acknowledgement/resource counts. In the one-agent failure and expiry cases, eleven agents consume one unit each and the affected agent consumes none (36 initial, 25 remaining); the duplicate case completes all twelve tasks (36 initial, 24 remaining). The host declares `Completed` only when the coordinator has evidence for all twelve unique agent acknowledgements. A handler failure is terminally `Failed`; an expired task is `Incomplete`. Neither is retried and neither receives a fabricated acknowledgement.

The normal all-success path remains available through `AutonomousSwarmSimulation.RunAsync()`. This fault-injection entry point is a deterministic reliability proof over the same message fabric and hierarchical participant topology, not a production scheduler or recovery policy. Duplicate suppression is enqueue idempotency, not exactly-once execution.

## Authority and safety boundaries

This is a simulation of software participants only. The task is a virtual survey-cell label; it does not represent navigation, sensing, target selection, physical movement, or actuation. There is no hardware adapter, network transport, arbitrary command execution, dynamic code loading, or external side effect.

The host defines and registers every participant and its accepted message vocabulary. The message fabric transports messages; it does not grant authority. Agent state is owned by each agent participant. Sub-swarm participants may assign only the fixed benign task shape to their pre-registered agents. The coordinator observes acknowledgements; it does not execute agent instructions.

Capability discovery may identify a gap, but it must remain a proposal. Any future specialist-role activation requires a separate host-controlled human approval checkpoint and explicit bounded role definition; a detected gap must never activate a role by itself.

## Verification

The contract checks cover:

- Default bounded partition, exact task and acknowledgement counts, resource accounting, deterministic replay, and rejection of dimensions above configured limits
- Injected participant-handler failure reported as `Failed`, with no acknowledgement
- Delayed task expiry reported as `Incomplete`, with no acknowledgement
- Identical duplicate task attempt suppressed, with one delivery and one acknowledgement

The underlying fabric remains process-local, volatile, single-host, and sequential. This proof does not establish durable messaging, cross-process coordination, broker failover, or production distributed operation.
