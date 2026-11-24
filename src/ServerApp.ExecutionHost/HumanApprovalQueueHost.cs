using Microsoft.AspNetCore.Routing;
using TheSingularityWorkshop.Execution;
using TheSingularityWorkshop.Execution.Approvals;

namespace ServerApp.ExecutionHost;

/// <summary>
/// ServerApp-owned lifetime wrapper for Execution's durable human approval queue.
/// The web host supplies its real authentication/authorization policy when mapping routes.
/// </summary>
public sealed class HumanApprovalQueueHost
{
    public HumanApprovalQueueHost(string journalPath)
    {
        Store = new JsonlHumanApprovalRequestStore(journalPath);
    }

    /// <summary>The authoritative durable queue used by the HTTP projection.</summary>
    public JsonlHumanApprovalRequestStore Store { get; }

    /// <summary>
    /// Maps the GUI-facing pending-approval query and human-decision endpoints.
    /// No endpoint is mapped unless the host supplies an explicit authorizer.
    /// </summary>
    public void MapEndpoints(IEndpointRouteBuilder endpoints, IHumanApprovalQueueAuthorizer authorizer)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(authorizer);
        endpoints.MapHumanApprovalQueue(Store, authorizer);
    }
}
