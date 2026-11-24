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
