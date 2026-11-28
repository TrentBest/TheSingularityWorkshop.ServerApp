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

### Authenticated producer handoff and authoritative lifecycle

The workflow-evidence handoff is now a real component path, not a direct host-side packet fabrication path:

1. `ServerAppCiAiEvidenceHost.AcceptPublishedEvidenceAsync` accepts the exact `ciai.workflow-evidence` artifact-ready notice through a request-scoped `ICiAiArtifactNoticeAuthenticator`. The ingress validates it against the persisted dispatch mapping and durable receipt ledger.
2. `CiAiArtifactRetrievalCoordinator` retrieves the immutable packet through the host-supplied `ICiAiArtifactTransport`; size, expiry, media type, and SHA-256 are checked before the packet is consumed.
3. ServerApp independently observes the exact provider run, downloads the provenance and test-results artifacts using the provider event/head SHA, reads the protected workflow definition at the mapped workflow-definition SHA, and calls Execution's V1 acceptance service. The producer packet is not trusted merely because its notice was authenticated.
4. The host also parses the exact TRX payload through CiAi's report contract and creates an Execution-owned `CiAiHostObservedSourceArtifactBinding` from the provider artifact, accepted packet, observed job, and extracted TRX bytes.
5. `ServerAppAutonomousDevelopmentHost.ApplyAuthoritativeWorkflowEvidence` rebinds the accepted V1 packet to the persisted task lease and mapping, appends the exact packet digest to the durable repair-attempt journal, and delegates the decision to Execution's deterministic lifecycle policy.

Only the authoritative packet can create `ReviewReady`, `RepairRequired`, `Blocked`, or CODE RED. Test-results reports, model output, and caller-supplied verdicts cannot create or alter those states.

The provider event/head SHA and trusted workflow-definition SHA are distinct. Both provider artifact downloads use the exact event/head SHA; the workflow definition is checked separately against the protected revision in the dispatch mapping.

### Separate diagnostic report handoff

The `ciai.test-results.v1` report is a second, separate artifact-ready notice. `ServerAppCiAiEvidenceHost.AcceptPublishedDiagnosticReportAsync` authenticates and retrieves it through the same receipt and transport boundaries, then compares the exact report bytes to the host's independently parsed report from the verified TRX archive. A producer report that differs from the provider artifact is rejected.

`ServerAppAutonomousDevelopmentHost.ProcessCiAiDiagnosticReportAsync` accepts only that verified report artifact together with the host-created source-artifact binding. It consumes the report through `PersistentWorkTaskCoordinator.ConsumeCiAiDiagnosticReport` and calls `ExecutionAgentTurnOrchestrator.InvokeRepairAndRouteAsync`. That path requires the task already be `RepairRequired` and verifies the report's exact candidate, run/attempt, source-artifact identity, and authoritative workflow-evidence digest before invoking HeadlessAi.

The report is diagnostic context only. It cannot satisfy a required check, declare a successful build, authorize a repair, or bypass the V1 lifecycle gate. HeadlessAi's proposed operations still pass through the host's closed capability vocabulary, policy, and human-approval queue.

### Dispatch identity and idempotency

Both producer enqueuers preserve the original request ID and operation ID from the persisted dispatch mapping. The base dispatch idempotency key is deterministically scoped by artifact capability, so `ciai.workflow-evidence` and `ciai.test-results` receive distinct, stable receipt keys for the same dispatch. Execution recomputes and validates those keys; they are not synthesized from run IDs or report hashes.

### Remaining live-host boundary

These are component and contract-tested handoff paths, not a claim of an operational autonomous pipeline. The current CLI is not yet an ASP.NET web host; authenticated notice routes and approval routes are not publicly listening until a real web host maps them with configured request-scoped authenticators and an `IHumanApprovalQueueAuthorizer`. Concrete GitHub API credentials, artifact storage/transport credentials, durable production composition, and the end-to-end red → repair → green cycle still require live integration and proof. No provider identity or credential system is assumed, and no autonomous publish, release, deployment, or branch deletion is enabled.
