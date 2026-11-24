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

`ServerAppAutonomousDevelopmentHost` composes the persistent Execution task coordinator, one configured HeadlessAi turn coordinator, the host-owned operation policy/executor, and this approval queue. Its `AgentTurns` entry point performs one bounded model turn and then routes the structured operation proposals through the closed Execution action vocabulary, capability checks, or the pending human queue. The host still supplies the verified CI evidence references and canonical response schema when invoking a turn. A live CI artifact-ready trigger and production provider/authentication configuration remain separate host-wiring steps.


## Verified build report entry point

`ServerAppAutonomousDevelopmentHost.ProcessVerifiedBuildReportAsync` is the first bounded report-to-model entry point. The caller supplies the accepted report identity, SHA-256, exact verified JSON payload, and independently verified evidence references. The host wrapper recomputes the payload digest using a fixed-time comparison, enforces size bounds, packages the report into the canonical HeadlessAi response contract, invokes the configured turn coordinator, and routes the returned structured operations through the host policy/capability/approval path.

The caller must only invoke this method after authenticating the CiAi notice and independently verifying the report's manifest, digest, provenance, repository, run, and candidate SHA. Automatic invocation from the CiAi artifact-ready callback is not yet wired; this method is the bounded handoff point for that integration.
