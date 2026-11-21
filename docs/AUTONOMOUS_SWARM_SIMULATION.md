# Autonomous Swarm Simulation

## Status

The first deterministic virtual coordination slice is implemented on `development`. It uses the existing `ServerApp.Core.SimulationMessageFabric`; it does not introduce a second queue or execution authority.

## Scenario

`ServerApp.Simulation.AutonomousSwarmSimulation.RunAsync()` creates a bounded world of virtual survey agents:

- Up to 8 sub-swarms, with up to 8 agents per sub-swarm
- One host-created coordinator, one participant per sub-swarm, and one participant per agent
- A finite message vocabulary: group assignment, benign task assignment, and acknowledgement
- Three virtual delivery steps: coordinator to sub-swarm, sub-swarm to agents, agents to coordinator
- Stable message IDs and deterministic ordering
- A fixed per-agent resource budget; one unit is consumed per assigned survey task
- Immutable result snapshots and a fabric trace

The default scenario contains 3 sub-swarms and 12 agents. Each agent receives one harmless virtual survey-cell task and acknowledges it. The run checks that all assignments are acknowledged by the coordinator and that resource accounting is conserved.

## Authority and safety boundaries

This is a simulation of software participants only. The task is a virtual survey-cell label; it does not represent navigation, sensing, target selection, physical movement, or actuation. There is no hardware adapter, network transport, arbitrary command execution, dynamic code loading, or external side effect.

The host defines and registers every participant and its accepted message vocabulary. The message fabric transports messages; it does not grant authority. Agent state is owned by each agent participant. Sub-swarm participants may assign only the fixed benign task shape to their pre-registered agents. The coordinator observes acknowledgements; it does not execute agent instructions.

## Verification

The contract checks cover the default bounded partition, exact task and acknowledgement counts, resource accounting, deterministic replay, and rejection of dimensions above configured limits.

The underlying fabric remains process-local, volatile, single-host, and sequential. This proof does not establish durable messaging, cross-process coordination, broker failover, or production distributed operation.

## Next evidence

Further work should add explicit failure injection (participant failure, delayed/expired messages, duplicate task attempts), inspectable recovery/failure outcomes, capability-gap proposals, and a separate human approval checkpoint for any specialist-role activation. Those capabilities must remain fail-closed; a discovered capability gap must never activate a role by itself.
