<!-- The Singularity Workshop moniker -->
<p align="center"><a href="https://github.com/TrentBest"><img src="https://raw.githubusercontent.com/TrentBest/TheSingularityWorkshop.Visualize/development/docs/assets/moniker.svg" alt="The Singularity Workshop animated moniker" width="720" /></a></p>

# Server Role Manifest — First Contract

## Purpose

A ServerApp server-role manifest describes **one requested server instance**: its role identifier, instance identity, declared dependencies, and resource requirements. It is distinct from the FSM_COS `RuntimeManifest`, which identifies MicroBundles and versions to compose into a runtime.

The ServerApp manifest asks: *what server role should be provisioned, with what declared capacity and prerequisites?* The FSM_COS manifest asks: *which MicroBundles and versions should be composed?* A server-role adapter may invoke FSM_COS internally when that role requires composition, but the two contracts remain separate.

## Version 1 shape

The versioned machine-readable schema is [server-role-manifest.v1.schema.json](../schemas/server-role-manifest.v1.schema.json). The first proof uses this JSON shape:

```json
{
  "SchemaVersion": 1,
  "InstanceId": "local-diagnostic-001",
  "RoleId": "diagnostic.ready",
  "Dependencies": [],
  "Resources": {
    "CpuCores": 1,
    "MemoryMiB": 128
  }
}
```

| Field | Meaning |
|---|---|
| `SchemaVersion` | Required schema version. The first implementation accepts only 1. |
| `InstanceId` | Caller-assigned identity for this requested instance. |
| `RoleId` | Stable role key resolved against an explicitly registered adapter. |
| `Dependencies` | Declared prerequisite identifiers. Resolution/ordering is not implemented in this first slice. |
| `Resources.CpuCores` | Requested CPU core capacity; validation currently accepts 1–1024. |
| `Resources.MemoryMiB` | Requested memory in MiB; validation currently accepts 64–1,048,576. |

Unknown JSON properties are rejected. The JSON Schema is the published structural contract; the runtime validator enforces the current implementation's supported version, identifier rules, duplicate-dependency rule, and resource bounds. The first implementation also rejects missing/invalid identifiers, duplicate dependency names, unsupported schema versions, and out-of-range resource requests.

## Provisioning boundary

The initial local coordinator performs these steps:

1. Validate the manifest.
2. Resolve `RoleId` only against an adapter registered by the host.
3. Invoke that adapter with the manifest and cancellation token.
4. Report `Ready` only when the adapter explicitly reports readiness; otherwise report `Failed`.

The manifest cannot name an assembly, executable, shell command, URL, or arbitrary code payload. There is no dynamic code loading, network listener, distributed placement, dependency resolver, persistent registry, or hardware resource reservation in this proof.

## Role adapter contract

`IServerRoleAdapter` is the explicit host extension point. The host constructs and registers trusted adapters. Each adapter declares one stable `RoleId` and reports whether its role has reached readiness. Adapter startup is not considered successful merely because a process or method was invoked.

The sample `diagnostic.ready` adapter is deliberately in-process and non-networked. It proves manifest parsing, validation, role lookup, activation dispatch, and readiness reporting—not a production server.

## Run locally

With .NET 8 SDK installed, from the repository root:

```bash
dotnet run --project src/ServerApp.Cli/ServerApp.Cli.csproj -- examples/diagnostic-ready.serverapp.json
dotnet run --project tests/ServerApp.ContractChecks/ServerApp.ContractChecks.csproj
```

The contract-check executable uses no third-party test packages. It checks successful activation, unsupported schema rejection, unknown-role fail-closed behavior, duplicate dependencies, and invalid resource bounds.

## Status and next contract decisions

**Source-added, not yet build-verified in this environment.** The implementation is a first local proof, not yet a production manifest contract.

The next design steps are to define dependency semantics (service dependency versus MicroBundle dependency), resource units/limits and actual reservation, manifest provenance/signature and authorization, idempotency/reconciliation, adapter lifecycle and health, and the separation between requested capacity and measured capacity. Distributed placement should wait until the local lifecycle contract is stable.

---
<!-- THE SINGULARITY WORKSHOP FOOTER -->
<p align="center"><img src="https://raw.githubusercontent.com/TrentBest/FSM_API/master/Documentation/Branding/TheSingularityWorkshop.png" alt="The Singularity Workshop avatar" width="200" /></p>
