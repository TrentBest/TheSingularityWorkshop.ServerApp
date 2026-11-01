<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# FSM_Foundry Integration Boundary

**Status: source-pinned FSM_Foundry → FSM_COS → repository-backed artifact preparation → role-adapter workflow implemented; latest CI run [#41362237960](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp/actions/runs/41362237960) passed.** ServerApp submits a three-step provisioning workflow to FSM_Foundry's actual `WorkflowExecutionCoordinator`. The workflow validates an allowlisted role definition, prepares the candidate's exact immutable MicroBundle dependency closure from MicroBundleRepository, composes only after preparation succeeds, and passes the returned `RuntimeAssembly` to a host-owned role adapter. Contract fixtures cover dependency-first composition, concurrent candidate isolation, verified artifact preparation, atomic catalog staging, and readiness gating. A local persistent filesystem artifact adapter is now available for development hosts. Remote/production storage authorization and provenance policy, a production server role process, and a distributed host remain incomplete.

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
  candidate-private IMicroBundleCatalog
  RepositoryBackedCompositionSession.PrepareAsync
  exact artifact addresses + SHA-256 + pinned dependency closure
  host-registered precompiled materializer allowlist
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

The first composed workflow host now exists in `FoundryComposedProvisioningHost`. Its handlers are registered by the host, not selected by client data. `ServerRoleRuntimeComposer` maps a validated `RoleId` to a host-owned `ServerRoleCompositionDefinition`, creates a fresh `IServerRoleCompositionSession` for that provisioning operation, constructs a separate `RuntimeManifest`, and composes through that session. Each session owns its own FSM_COS composer, catalog boundary, and runtime registry. `RepositoryBackedCompositionSession.PrepareAsync` observes immutable inventory, resolves each `(BundleId, Version)` to exactly one `MicroBundleArtifactAddress`, retrieves that exact address, relies on `MicroBundleArtifact` construction to verify SHA-256, and invokes only a host-registered precompiled factory keyed by the full address. The factory receives the immutable address—not artifact bytes—so the registry contract cannot turn downloaded bytes into an assembly/script loader. It traverses exact-version dependency requests, rejects unpinned or conflicting dependencies, and publishes the candidate catalog only after the entire closure has been retrieved and materialized. The live session is retained with the successful runtime; failed or cancelled activation forgets registry metadata and disposes the candidate session. `IServerRoleRuntimeAdapter` receives that assembly and is the host-owned activation seam. Current tests use deterministic in-memory bundles and a fixture adapter; a production runtime adapter remains future work.

- **ServerApp.Core** remains independent of Foundry and FSM_COS. It owns operational manifests, dependency planning, capacity placement, and fleet lifecycle contracts.
- **Foundry** owns workflow acceptance, sequential execution through explicitly registered handlers, run snapshots, cancellation, and structured workflow outcomes.
- **MicroBundleRepository.Core** owns exact immutable artifact addresses, retrieval, inventory observation, and SHA-256 verification at `MicroBundleArtifact` construction; it does not materialize executable code.
- **FSM_COS** owns MicroBundle composition. It resolves and validates a candidate-private catalog and returns a RuntimeAssembly to its host.
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
- [WorkflowDefinition.cs](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/blob/development/src/Foundry.Workflow/WorkflowDefinition.cs)
- [WorkflowHandlerRegistry.cs](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/blob/development/src/Foundry.Workflow/WorkflowHandlerRegistry.cs)
- [WorkflowExecutionContext.cs](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/blob/development/src/Foundry.Workflow/WorkflowExecutionContext.cs)
- [WorkflowRunner.cs](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/blob/development/src/Foundry.Workflow/WorkflowRunner.cs)
- [WorkflowExecutionCoordinator.cs](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/blob/development/src/Foundry.Workflow/WorkflowExecutionCoordinator.cs)
- [Foundry architecture](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry/blob/development/docs/ARCHITECTURE.md)

MicroBundleRepository Core source exposes `IMicroBundleRepository.GetAsync` for complete immutable addresses, `IMicroBundleRepositoryObserver.ListAsync` for artifact inventory, `MicroBundleArtifactResolver.ResolveExact` for unambiguous ID/version-to-hash resolution, and `MicroBundleArtifact` whose constructor validates the SHA-256 of retrieved bytes. These are storage/identity contracts, not a transport server or executable loader.

FSM_COS source exposes `FsmCos.Execute` / `TryExecute`, `RuntimeManifest`, and `RuntimeAssembly`. The assembly contains composed MicroBundles; it is not itself a started server process or a Foundry workflow. See [FsmCos.cs](https://github.com/TrentBest/TheSingularityWorkshop.FSM_COS/blob/development/src/FSM_COS/FsmCos.cs), [RuntimeManifest.cs](https://github.com/TrentBest/TheSingularityWorkshop.FSM_COS/blob/development/src/FSM_COS/RuntimeManifest.cs), and [RuntimeAssembly.cs](https://github.com/TrentBest/TheSingularityWorkshop.FSM_COS/blob/development/src/FSM_COS/RuntimeAssembly.cs).

## 4. Implemented composed workflow proof

`FoundryComposedProvisioningHost` submits a fixed, host-authored workflow with three sequential steps:

1. **Validate role:** validate and snapshot the operational manifest, resolve the approved role definition, and require a registered runtime adapter.
2. **Compose runtime:** create a candidate-scoped composition session; await its `PrepareAsync` to resolve, retrieve, hash-verify, and precompiled-materialize the complete pinned dependency closure; then construct a new FSM_COS `RuntimeManifest` from the approved root list and a host-assigned runtime ID and compose through the session's private `IFsmCos`/`IMicroBundleCatalog` pair.
3. **Activate role:** select the composed assembly in FSM_COS host metadata, pass the live `RuntimeAssembly` to the registered `IServerRoleRuntimeAdapter`, and report `Ready` only when the adapter returns `IsReady=true`.

The role definition catalog is host-owned and maps exact `RoleId` values to immutable root bundle/version requests. The client-facing `ServerRoleManifest` contains no bundle IDs, type names, assembly paths, or executable workflow steps. This prevents a provisioning request from choosing arbitrary MicroBundles or handlers.

The Foundry workflow context contains only a GUID correlation value. Live `RuntimeAssembly` references remain in a private host operation dictionary because Foundry context is explicitly bounded JSON data, not an object transport or runtime container.

Contract checks use the actual Foundry coordinator and FSM_COS implementation with in-memory fixtures. They prove that a dependency loads before its requested root, the composed assembly reaches the role adapter, missing bundle composition prevents activation, a not-ready adapter fails the workflow, two concurrent candidates can resolve different versions of the same BundleId from isolated catalogs, a second simultaneous preparation on one candidate is rejected without republishing its catalog, exact artifact retrieval and transitive version pins prepare successfully, and a missing transitive artifact prevents partial catalog publication or composition. The older simple `FoundryProvisioningHost` remains a Core-only adapter fixture path; `FoundryComposedProvisioningHost` is the integration path that exercises the full composition handoff.

Foundry workflow success means every registered step completed. ServerApp's `Ready` state is written only after the role adapter confirms readiness. FSM_COS registry selection is metadata, not execution. Neither a successful composition nor a successful workflow proves a production listener, health endpoint, or distributed host.

## 5. Dependency and build strategy

Foundry, FSM_COS, MicroBundleDomain, and MicroBundleRepository Core are built from pinned source; no new NuGet package is required or published. CI checks out these exact revisions:

| Repository | Commit |
|---|---|
| [Foundry](https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry) | `8bd3487a96af2cd7d667f62c4566a499bafe30dc` |
| [FSM_COS](https://github.com/TrentBest/TheSingularityWorkshop.FSM_COS) | `cad5775fe55a6a8d0b6df8bbd22017c1179141c4` |
| [FSM_API](https://github.com/TrentBest/FSM_API) | `4db572f5e0e74df028b01ee22fb393e2bd203eb6` |
| [FSM_UserIO](https://github.com/TrentBest/FSM_UserIO) | `db5b59d0521a341f97e6077e3508eb2bd7171607` |
| [MicroBundleDomain](https://github.com/TrentBest/TheSingularityWorkshop.MicroBundleDomain) | `d8c1f52d3550591aec2337ad4470bfadb20fb11e` |
| [MicroBundleRepository](https://github.com/TrentBest/TheSingularityWorkshop.MicroBundleRepository) | `2b23967262766a0466b263e0838afbf8affd234b` |

FSM_COS source mode expects the sibling directory name `_deps/TheSingularityWorkshop.MicroBundleDomain`; the CI checkout uses that exact name. The host project references `_deps/FSM_Foundry/src/Foundry.Workflow/Foundry.Workflow.csproj`, `_deps/FSM_COS/src/FSM_COS/FSM_COS.csproj`, `_deps/MicroBundleRepository/src/Core/TheSingularityWorkshop.MicroBundleRepository.Core/TheSingularityWorkshop.MicroBundleRepository.Core.csproj`, and `_deps/MicroBundleRepository/src/Local/TheSingularityWorkshop.MicroBundleRepository.Local/TheSingularityWorkshop.MicroBundleRepository.Local.csproj`. Build with `-p:WorkshopUseRepositorySources=true` so FSM_COS uses the pinned FSM_API, FSM_UserIO, and MicroBundleDomain source projects instead of package references.

For a local checkout from the ServerApp repository root:

```bash
git clone https://github.com/TrentBest/TheSingularityWorkshop.FSM_Foundry.git _deps/FSM_Foundry
git -C _deps/FSM_Foundry checkout 8bd3487a96af2cd7d667f62c4566a499bafe30dc
git clone https://github.com/TrentBest/TheSingularityWorkshop.FSM_COS.git _deps/FSM_COS
git -C _deps/FSM_COS checkout cad5775fe55a6a8d0b6df8bbd22017c1179141c4
git clone https://github.com/TrentBest/FSM_API.git _deps/FSM_API
git -C _deps/FSM_API checkout 4db572f5e0e74df028b01ee22fb393e2bd203eb6
git clone https://github.com/TrentBest/FSM_UserIO.git _deps/FSM_UserIO
git -C _deps/FSM_UserIO checkout db5b59d0521a341f97e6077e3508eb2bd7171607
git clone https://github.com/TrentBest/TheSingularityWorkshop.MicroBundleDomain.git _deps/TheSingularityWorkshop.MicroBundleDomain
git -C _deps/TheSingularityWorkshop.MicroBundleDomain checkout d8c1f52d3550591aec2337ad4470bfadb20fb11e
git clone https://github.com/TrentBest/TheSingularityWorkshop.MicroBundleRepository.git _deps/MicroBundleRepository
git -C _deps/MicroBundleRepository checkout 2b23967262766a0466b263e0838afbf8affd234b
dotnet build src/ServerApp.FoundryHost/ServerApp.FoundryHost.csproj --configuration Release -p:WorkshopUseRepositorySources=true
dotnet run --project tests/ServerApp.FoundryHost.ContractChecks/ServerApp.FoundryHost.ContractChecks.csproj --configuration Release -p:WorkshopUseRepositorySources=true
```

A floating branch checkout is not acceptable verification. `ServerApp.Core` remains independent of Foundry and FSM_COS. Latest CI run [#41362237960](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp/actions/runs/41362237960) passed all pinned-source checkouts, Core/CLI and Foundry host builds, and both contract-check executables, including lifecycle drain/stop/retry/cleanup coverage. Documentation warnings originate in pinned upstream source projects. No package publication is authorized.

## 6. Role lifecycle boundary

Successful activation now retains a host-owned `ServerRoleLifecycleController` when the registered adapter implements `IServerRoleLifecycleAdapter`. The host exposes `GetLifecycleController(runId)`, `DrainRoleAsync(runId)`, and `StopRoleAsync(runId)`. The controller serializes transitions, makes drain idempotent, automatically drains before stop, preserves a drained live session after a failed stop, and retries post-stop registry/session cleanup without repeating a confirmed role stop. Cleanup is performed only after the adapter confirms stop. Contract checks cover explicit drain, transient stop failure, retry, repeated stop, and exactly-once registry/session cleanup. This is a host lifecycle contract exercised with an in-process fixture; it is not yet a production process adapter, service manager, or public endpoint. The adapter is responsible for the meaning and evidence of drain/stop; FSM_COS composition cleanup is a separate subsequent action and does not claim arbitrary bundle unloading.

## 7. Non-goals of the first proof

The current proof does not establish a distributed queue, durable cross-process workflow store, remote command execution, production server listener, concrete persistent/network repository deployment, arbitrary assembly loading, hardware reservation, GPU-aware placement, multi-host scheduling, live health monitoring, production process supervision, arbitrary bundle unloading, migration, or production deployment. The host-side resolver consumes injected `IMicroBundleRepository` and inventory-observer contracts. `LocalFileSystemCompositionSessionFactory` now supplies both contracts from one explicitly rooted `FileSystemMicroBundleRepository`; this is a development adapter, not a production trust boundary. The host receives no write API through the session factory, and this integration does not expose a REST endpoint, deploy storage, or define remote authorization/provenance. Exact-address retrieval, SHA-256 verification, pinned closure preparation, and precompiled materialization are implemented and contract-tested; operational authentication, authorization, provenance policy, and storage deployment remain separate requirements.

## 7. Progress gate

- [x] Foundry selected as the mandatory workflow execution substrate.
- [x] Actual Foundry and FSM_COS source boundaries inspected.
- [x] ServerApp integration tracked in [issue #1](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp/issues/1).
- [x] Exact Foundry, FSM_COS, MicroBundleDomain, and MicroBundleRepository Core source dependencies pinned in CI and local build instructions.
- [x] Host integration project calls the actual Foundry coordinator and registered-handler APIs.
- [x] Contract checks cover ready, failed/not-ready, duplicate pending instance, cancellation before start, and cooperative cancellation while a role handler is running.
- [x] FSM_COS composition handoff implemented through a separate host-owned RuntimeManifest and approved role-definition catalog.
- [x] RuntimeAssembly passed to an explicit ServerApp role adapter; Foundry workflow context carries only an opaque operation ID.
- [x] Contract fixtures prove dependency-first composition, assembly handoff, composition failure preventing activation, and readiness gating.
- [x] Candidate-scoped session abstraction gives every provisioning operation its own FSM_COS composer/catalog boundary and runtime registry; successful sessions remain retained with their runtime.
- [x] Repository-backed candidate preparation resolves exact immutable artifact addresses, retrieves and SHA-256 verifies each artifact, enforces exact-version transitive dependencies, and atomically publishes a private catalog only after full preparation.
- [x] Negative repository contracts reject mismatched returned artifact addresses, materializers returning a different bundle identity, and unpinned transitive dependencies.
- [x] Executable materialization is restricted to host-registered precompiled factories keyed by the complete artifact address; factories receive no artifact bytes, and downloaded bytes are never loaded as assemblies or scripts.
- [x] Concurrent candidate fixture covers different versions of the same BundleId without cross-candidate catalog contamination; same-session concurrent preparation is rejected and catalog publication is serialized against disposal.
- [x] CI run [#41341712499](https://github.com/TrentBest/TheSingularityWorkshop.ServerApp/actions/runs/41341712499) passed with six exact source dependencies pinned.
- [x] Core contract checks passed 23/23; the FSM_Foundry/FSM_COS/repository host contract-check executable passed.
- [x] Add an explicitly rooted local filesystem MicroBundleRepository adapter for development composition sessions; Core retrieval and inventory are supplied by the same repository instance.
- [ ] Select and integrate a production persistent/network MicroBundleRepository adapter with authentication, authorization, provenance policy, and operational observability.
- [x] Expose a host-owned lifecycle handle and drain/stop operations for successfully activated lifecycle-capable roles; contract-test drain-before-stop, retry after transient stop failure, idempotent repeated stop, and exactly-once post-stop cleanup.
- [ ] Replace the fixture role adapter with a real server role process and production supervision contract.


---

<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop" width="200" /></p>
