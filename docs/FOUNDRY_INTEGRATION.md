<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Foundry Integration Boundary

**Status: first workflow-host integration implemented; FSM_COS composition and production role activation remain unimplemented.** ServerApp now submits provisioning requests to the actual Foundry `WorkflowExecutionCoordinator` through `ServerApp.FoundryHost`. This proves Foundry owns the workflow lifecycle; it does not yet prove a composed MicroBundle runtime is started.

## 1. The three jobs are different

A server-role manifest, an FSM_COS runtime manifest, and a Foundry workflow definition are three different contracts:

| Contract | Owner | Meaning |
|---|---|---|
| ServerRoleManifest | ServerApp | Which operational server role is requested, its instance identity, server-instance dependencies, and resource requirements |
| RuntimeManifest | FSM_COS | Which versioned MicroBundles should be composed from a host-supplied catalog |
| WorkflowDefinition | Foundry | Which sequential, named, host-registered operations should execute for one workflow |

Do not serialize one contract as another or infer one from matching field names.

## 2. Ownership and handoff

```text
Provisioning request
       |
       v
ServerApp.Core
  validate role manifest
  resolve server-instance dependencies
  plan host capacity
       |
       v
ServerApp Foundry host adapter
  create bounded WorkflowDefinition
  register allowlisted host handlers
  submit / start / observe / cancel
       |
       +------------------------------+
       |                              |
       v                              v
FSM_COS composition handler     ServerApp role host handler
  RuntimeManifest                 consumes approved composition
  host-supplied catalog            and activates role
  RuntimeAssembly                         |
       |                                  v
       +--------------------------> role readiness
                                          |
                                          v
                                  ServerApp operational state
```

The Foundry workflow host adapter and its provisioning handler now exist. The FSM_COS composition handler and a production RuntimeAssembly-consuming role host remain planned; the current role adapter contract is the existing ServerApp in-process activation seam.

- **ServerApp.Core** remains independent of Foundry and FSM_COS. It owns operational manifests, dependency planning, capacity placement, and fleet lifecycle contracts.
- **Foundry** owns workflow acceptance, sequential execution through explicitly registered handlers, run snapshots, cancellation, and structured workflow outcomes.
- **FSM_COS** owns MicroBundle composition. It resolves and validates a host-supplied catalog and returns a RuntimeAssembly to its host.
- **The ServerApp host/integration layer** connects these contracts. It must not move orchestration into ServerApp.Core or create a second workflow runner.

## 3. What current source actually supports

The Foundry development source exposes:
- `WorkflowDefinition`: version-1 data-only sequential steps, each naming a handler key.
- `WorkflowHandlerRegistry`: host-owned allowlist of handlers.
- `WorkflowExecutionContext`: bounded JSON inputs and outputs; data values do not grant authority.
- `WorkflowExecutionCoordinator`: accepts a run, starts it once, exposes immutable lifecycle snapshots, and supports cooperative cancellation.
- `WorkflowRunner`: validates the complete definition before handler side effects, executes handlers sequentially, stops after failure, and reports structured outcomes.

Source references:
- [WorkflowDefinition.cs](https://github.com/TrentBest/TheSingularityWorkshop.Foundry/blob/development/src/Foundry.Workflow/WorkflowDefinition.cs)
- [WorkflowHandlerRegistry.cs](https://github.com/TrentBest/TheSingularityWorkshop.Foundry/blob/development/src/Foundry.Workflow/WorkflowHandlerRegistry.cs)
- [WorkflowExecutionContext.cs](https://github.com/TrentBest/TheSingularityWorkshop.Foundry/blob/development/src/Foundry.Workflow/WorkflowExecutionContext.cs)
- [WorkflowRunner.cs](https://github.com/TrentBest/TheSingularityWorkshop.Foundry/blob/development/src/Foundry.Workflow/WorkflowRunner.cs)
- [WorkflowExecutionCoordinator.cs](https://github.com/TrentBest/TheSingularityWorkshop.Foundry/blob/development/src/Foundry.Workflow/WorkflowExecutionCoordinator.cs)
- [Foundry architecture](https://github.com/TrentBest/TheSingularityWorkshop.Foundry/blob/development/docs/ARCHITECTURE.md)

FSM_COS source exposes `FsmCos.Execute` / `TryExecute`, `RuntimeManifest`, and `RuntimeAssembly`. The assembly contains composed MicroBundles; it is not itself a started server process or a Foundry workflow. See [FsmCos.cs](https://github.com/TrentBest/TheSingularityWorkshop.FSM_COS/blob/development/src/FSM_COS/FsmCos.cs), [RuntimeManifest.cs](https://github.com/TrentBest/TheSingularityWorkshop.FSM_COS/blob/development/src/FSM_COS/RuntimeManifest.cs), and [RuntimeAssembly.cs](https://github.com/TrentBest/TheSingularityWorkshop.FSM_COS/blob/development/src/FSM_COS/RuntimeAssembly.cs).

## 4. First integration proof

The first vertical slice should submit one provisioning operation through the actual Foundry coordinator. Its registered handlers should be narrow and deterministic:

1. Validate and snapshot the ServerRoleManifest.
2. Resolve its already-defined operational dependency and placement plan.
3. Invoke an approved composition provider with a separately constructed RuntimeManifest and host-supplied catalog.
4. Pass the resulting RuntimeAssembly to an explicit role host adapter.
5. Report Ready only when that adapter reports actual role readiness.

The initial proof may use deterministic in-memory catalogs and role adapters, but it must reference and execute the actual Foundry workflow implementation. A fake Foundry class or renamed diagnostic adapter is not acceptable.

Foundry workflow success means the registered workflow steps completed. It does **not** automatically mean a role is Ready. ServerApp must preserve this distinction in its result model.

The proof must cover invalid workflow preflight, unregistered handler rejection, failure stopping later steps, correlated cancellation, duplicate/re-entry protection, composition failure, role activation failure, and Ready only after an explicit readiness result.

## 5. Dependency and build strategy

Foundry.Workflow currently has a .NET 8 project and no established published package contract. Do not invent a NuGet version or publish it to make this integration compile.

Before adding a project reference, choose and test a reproducible source-consumption strategy that pins an exact Foundry commit and builds its actual project. The strategy must work in CI and be understandable to a developer cloning ServerApp. A floating branch checkout is not acceptable evidence. Keep any cross-repository source integration in a dedicated host/integration project, not ServerApp.Core.

The same rule applies to FSM_COS: use its existing package only when the required public package contract is sufficient, or pin/build the exact source revision when repository-source behavior is required. No package publication is authorized by this integration plan.

## 6. Non-goals of the first proof

The first proof does not establish a distributed queue, durable cross-process workflow store, remote command execution, production server listener, arbitrary assembly loading, hardware reservation, GPU-aware placement, multi-host scheduling, live health monitoring, migration, or production deployment. Those require separate contracts and evidence.

## 7. Progress gate

- [x] Foundry selected as the mandatory workflow execution substrate.
- [x] Actual Foundry and FSM_COS source boundaries inspected.
- [x] ServerApp integration tracked in [issue #1](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp/issues/1).
- [x] Exact Foundry source dependency pinned in CI and local build instructions.
- [x] Host integration project calls the actual Foundry coordinator and registered-handler APIs.
- [x] Contract checks cover ready, failed/not-ready, duplicate pending instance, and cancellation before start.
- [ ] CI build/test result verified for the exact ServerApp commit and pinned Foundry revision.
- [ ] FSM_COS composition handoff and RuntimeAssembly-consuming role-host contract implemented.
- [ ] Runtime readiness is distinct from workflow completion in observable results.

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="200" /></p>
