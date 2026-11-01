<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Foundry Integration Boundary

**Status: first Foundry → FSM_COS → role-adapter vertical slice implemented in source; CI verification pending.** ServerApp submits a three-step provisioning workflow to Foundry's actual `WorkflowExecutionCoordinator`. The workflow validates an allowlisted role definition, composes its approved MicroBundle roots through FSM_COS, and passes the returned `RuntimeAssembly` to a host-owned role adapter. Contract fixtures prove dependency-first composition and readiness gating. This is not yet a production server process, repository-backed artifact delivery path, or distributed host.

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
ServerRoleManifest
       |
       v
Foundry WorkflowExecutionCoordinator
  1. validate role + resolve host-owned definition
  2. compose approved roots through FSM_COS
  3. activate through role-specific host adapter
       |                       |
       |                       +--> WorkflowExecutionContext
       |                            carries only operation ID (JSON)
       v
ServerRoleRuntimeComposer
  RuntimeManifest (separate contract)
  host-supplied IMicroBundleCatalog
       |
       v
FSM_COS FsmCos.Execute
  dependency closure -> Load -> bounded Arbitrate
       |
       v
RuntimeAssembly (live object, host-owned state)
       |
       v
IServerRoleRuntimeAdapter.StartAsync(manifest, assembly)
       |
       v
explicit role readiness -> ServerApp result
```

The first composed workflow host now exists in `FoundryComposedProvisioningHost`. Its handlers are registered by the host, not selected by client data. `ServerRoleRuntimeComposer` maps a validated `RoleId` to a host-owned `ServerRoleCompositionDefinition`, constructs a separate `RuntimeManifest`, calls `RuntimeExperienceHost.ComposeAndRegister`, and returns the composed assembly. `IServerRoleRuntimeAdapter` receives that assembly and is the host-owned activation seam. Current tests use deterministic in-memory bundles and a fixture adapter; a production runtime adapter remains future work.

- **ServerApp.Core** remains independent of Foundry and FSM_COS. It owns operational manifests, dependency planning, capacity placement, and fleet lifecycle contracts.
- **Foundry** owns workflow acceptance, sequential execution through explicitly registered handlers, run snapshots, cancellation, and structured workflow outcomes.
- **FSM_COS** owns MicroBundle composition. It resolves and validates a host-supplied catalog and returns a RuntimeAssembly to its host.
- **The ServerApp host/integration layer** connects these contracts. It must not move orchestration into ServerApp.Core or create a second workflow runner.
- **FSM_COS `RuntimeExperienceHost`** retains and selects composition results in host metadata. Its `TryActivate` method does not start execution; only the ServerApp role adapter can establish operational readiness.
- **Foundry workflow context** is bounded JSON-only data. A live `RuntimeAssembly` is never serialized into it; the workflow carries only an opaque operation ID, while the host retains the assembly in private per-operation state.

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

## 4. Implemented composed workflow proof

`FoundryComposedProvisioningHost` submits a fixed, host-authored workflow with three sequential steps:

1. **Validate role:** validate and snapshot the operational manifest, resolve the approved role definition, and require a registered runtime adapter.
2. **Compose runtime:** construct a new FSM_COS `RuntimeManifest` from the approved root list and a host-assigned runtime ID; compose through the injected `IFsmCos` instance, which owns its host-supplied `IMicroBundleCatalog`.
3. **Activate role:** select the composed assembly in FSM_COS host metadata, pass the live `RuntimeAssembly` to the registered `IServerRoleRuntimeAdapter`, and report `Ready` only when the adapter returns `IsReady=true`.

The role definition catalog is host-owned and maps exact `RoleId` values to immutable root bundle/version requests. The client-facing `ServerRoleManifest` contains no bundle IDs, type names, assembly paths, or executable workflow steps. This prevents a provisioning request from choosing arbitrary MicroBundles or handlers.

The Foundry workflow context contains only a GUID correlation value. Live `RuntimeAssembly` references remain in a private host operation dictionary because Foundry context is explicitly bounded JSON data, not an object transport or runtime container.

Contract checks use the actual Foundry coordinator and FSM_COS implementation with in-memory fixtures. They prove that a dependency loads before its requested root, the composed assembly reaches the role adapter, missing bundle composition prevents activation, and a not-ready adapter fails the workflow. The older simple `FoundryProvisioningHost` remains a Core-only adapter fixture path; `FoundryComposedProvisioningHost` is the integration path that exercises the full composition handoff.

Foundry workflow success means every registered step completed. ServerApp's `Ready` state is written only after the role adapter confirms readiness. FSM_COS registry selection is metadata, not execution. Neither a successful composition nor a successful workflow proves a production listener, health endpoint, or distributed host.

## 5. Dependency and build strategy

Both Foundry and FSM_COS are built from pinned source; no new NuGet package is required or published. CI checks out these exact revisions:

| Repository | Commit |
|---|---|
| [Foundry](https://github.com/TrentBest/TheSingularityWorkshop.Foundry) | `8bd3487a96af2cd7d667f62c4566a499bafe30dc` |
| [FSM_COS](https://github.com/TrentBest/TheSingularityWorkshop.FSM_COS) | `cad5775fe55a6a8d0b6df8bbd22017c1179141c4` |
| [FSM_API](https://github.com/TrentBest/FSM_API) | `4db572f5e0e74df028b01ee22fb393e2bd203eb6` |
| [FSM_UserIO](https://github.com/TrentBest/FSM_UserIO) | `db5b59d0521a341f97e6077e3508eb2bd7171607` |
| [MicroBundleDomain](https://github.com/TrentBest/TheSingularityWorkshop.MicroBundleDomain) | `d8c1f52d3550591aec2337ad4470bfadb20fb11e` |

FSM_COS source mode expects the sibling directory name `_deps/TheSingularityWorkshop.MicroBundleDomain`; the CI checkout uses that exact name. The host project references `_deps/Foundry/src/Foundry.Workflow/Foundry.Workflow.csproj` and `_deps/FSM_COS/src/FSM_COS/FSM_COS.csproj`. Build with `-p:WorkshopUseRepositorySources=true` so FSM_COS uses the pinned FSM_API, FSM_UserIO, and MicroBundleDomain source projects instead of package references.

For a local checkout from the ServerApp repository root:

```bash
git clone https://github.com/TrentBest/TheSingularityWorkshop.Foundry.git _deps/Foundry
git -C _deps/Foundry checkout 8bd3487a96af2cd7d667f62c4566a499bafe30dc
git clone https://github.com/TrentBest/TheSingularityWorkshop.FSM_COS.git _deps/FSM_COS
git -C _deps/FSM_COS checkout cad5775fe55a6a8d0b6df8bbd22017c1179141c4
git clone https://github.com/TrentBest/FSM_API.git _deps/FSM_API
git -C _deps/FSM_API checkout 4db572f5e0e74df028b01ee22fb393e2bd203eb6
git clone https://github.com/TrentBest/FSM_UserIO.git _deps/FSM_UserIO
git -C _deps/FSM_UserIO checkout db5b59d0521a341f97e6077e3508eb2bd7171607
git clone https://github.com/TrentBest/TheSingularityWorkshop.MicroBundleDomain.git _deps/TheSingularityWorkshop.MicroBundleDomain
git -C _deps/TheSingularityWorkshop.MicroBundleDomain checkout d8c1f52d3550591aec2337ad4470bfadb20fb11e
dotnet build src/ServerApp.FoundryHost/ServerApp.FoundryHost.csproj --configuration Release -p:WorkshopUseRepositorySources=true
dotnet run --project tests/ServerApp.FoundryHost.ContractChecks/ServerApp.FoundryHost.ContractChecks.csproj --configuration Release -p:WorkshopUseRepositorySources=true
```

A floating branch checkout is not acceptable verification. `ServerApp.Core` remains independent of Foundry and FSM_COS. No package publication is authorized.

## 6. Non-goals of the first proof

The current proof does not establish a distributed queue, durable cross-process workflow store, remote command execution, production server listener, repository-backed artifact acquisition, content-hash verification, arbitrary assembly loading, hardware reservation, GPU-aware placement, multi-host scheduling, live health monitoring, role stop/unload, migration, or production deployment. FSM_COS's current catalog is an in-process resolution contract; it does not download or verify artifact bytes. A future repository adapter must preserve BundleId + Version + ContentHash and prove retrieval, verification, and materialization separately before composition.

## 7. Progress gate

- [x] Foundry selected as the mandatory workflow execution substrate.
- [x] Actual Foundry and FSM_COS source boundaries inspected.
- [x] ServerApp integration tracked in [issue #1](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp/issues/1).
- [x] Exact Foundry source dependency pinned in CI and local build instructions.
- [x] Host integration project calls the actual Foundry coordinator and registered-handler APIs.
- [x] Contract checks cover ready, failed/not-ready, duplicate pending instance, cancellation before start, and cooperative cancellation while a role handler is running.
- [x] FSM_COS composition handoff implemented through a separate host-owned RuntimeManifest and approved role-definition catalog.
- [x] RuntimeAssembly passed to an explicit ServerApp role adapter; Foundry workflow context carries only an opaque operation ID.
- [x] Contract fixtures prove dependency-first composition, assembly handoff, composition failure preventing activation, and readiness gating.
- [ ] CI build and both contract-check executables pass for the latest exact ServerApp commit and all pinned source revisions.
- [ ] Replace the in-memory catalog and fixture role adapter with trusted repository-backed artifacts and a real server role host.

---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="200" /></p>
