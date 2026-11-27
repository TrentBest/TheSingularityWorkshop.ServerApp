# ADP Human Approval Host

ServerApp now has a host-lifetime wrapper for Execution's durable human-approval queue.

## Host composition

`HumanApprovalQueueHost` opens the configured JSONL journal and maps the Execution ASP.NET Core approval routes:

- `GET /api/v1/adp/approvals` — list pending requests; optional `repository=owner/name` filter.
- `POST /api/v1/adp/approvals/{approvalId}/decision` — record an explicit `approve` or `reject` decision and required reason.

The host must supply an `IHumanApprovalQueueAuthorizer` when mapping. This adapter intentionally does not invent user authentication, grant anonymous access, or ship a default identity. A real ServerApp web host must implement that interface against its configured authentication and authorization system before exposing the routes.

## GUI contract

The GUI reads the pending request projection from the GET endpoint and renders the repository, task/correlation, candidate SHA, requesting model, operation, rationale, exact proposed arguments, digest, expiry, and status. A human's explicit decision is sent to the POST endpoint; the GUI then refreshes the queue from the server.

The model cannot approve its own proposal. A decision is not itself a repository capability. After approval, the ADP host must revalidate the exact task and candidate, check current policy, and issue a fresh narrow capability before executing the operation.

## Current boundary

This is the host composition adapter and durable queue/API integration. ServerApp's current CLI is not yet an ASP.NET web host, so these routes are not publicly listening endpoints until a web host maps them and provides the required authorizer. The GUI Pages site is not yet connected to a running API. No deployment or production authentication configuration is claimed.

## ADP turn composition

`ServerAppAutonomousDevelopmentHost` now composes:

- the persistent Execution task coordinator;
- the CiAi V1 evidence-to-lifecycle coordinator;
- one configured HeadlessAi turn coordinator;
- the host-owned operation policy/executor; and
- the durable human approval queue.

The persistent task coordinator and evidence lifecycle coordinator must share the **same** `JsonlWorkTaskRepairAttemptJournal` object instance. Create the journal once, then inject that instance into both components. Separate instances opened against the same path do not share the in-memory journal view.

### Authoritative CI lifecycle

The bounded evidence path is:

1. `ServerAppCiAiEvidenceHost.ObserveAndAcceptAsync` independently observes the exact provider run, retrieves the provenance and test-result artifacts, verifies the workflow definition against the registered protected revision, and accepts the canonical `ciai.workflow-evidence.v1` packet.
2. `ServerAppAutonomousDevelopmentHost.ApplyAuthoritativeWorkflowEvidence` rebinds that accepted packet to the persisted dispatch mapping, exact task lease, candidate SHA, provider event/head SHA, workflow-definition SHA, run ID/attempt, and registered required-check set. It appends the exact accepted packet digest to the durable repair-attempt journal and invokes Execution's deterministic lifecycle policy.
3. Only the authoritative packet can create `ReviewReady`, `RepairRequired`, `Blocked`, or CODE RED. Test-result reports, model output, and caller-supplied verdicts cannot create or alter those states.

The provider event/head SHA and trusted workflow-definition SHA are distinct. Both provenance and test-results artifact retrieval use the event/head SHA; the workflow-definition SHA is checked separately against the protected workflow revision.

### Diagnostic-to-repair turn

`ServerAppAutonomousDevelopmentHost.ProcessCiAiDiagnosticReportAsync` accepts an Execution-verified report artifact, not arbitrary report JSON. It asks the persistent task coordinator to consume `ciai.test-results.v1` against the durable authoritative attempt. It then calls `ExecutionAgentTurnOrchestrator.InvokeRepairAndRouteAsync`, which requires the task already be `RepairRequired` and verifies the diagnostic report's exact candidate, run/attempt, source-artifact identity, and authoritative workflow-evidence digest before invoking HeadlessAi.

The test-results report remains diagnostic context only. It cannot satisfy a required check, declare a successful build, authorize a repair, or bypass the V1 lifecycle gate. HeadlessAi's proposed operations still pass through the host's closed capability vocabulary, policy, and human-approval queue.

### Remaining live-host boundary

These are now component and contract-tested host composition paths, not a claim of an operational autonomous pipeline. The current CLI is not yet an ASP.NET web host; approval routes are not publicly listening until a real web host maps them with a configured `IHumanApprovalQueueAuthorizer`. Provider credentials, artifact transport, authenticated artifact-ready ingress, durable host composition, and the end-to-end red → repair → green cycle still require live integration and proof. No provider or identity system is assumed, and no autonomous publish, release, deployment, or branch deletion is enabled.
