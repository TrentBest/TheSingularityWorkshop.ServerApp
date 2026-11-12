namespace ServerApp.Core;

/// <summary>
/// Eligibility state for one immutable registered bot build. Registration and review
/// authorization are host-owned; these states are the domain vocabulary consumed by admission.
/// </summary>
public enum BotBuildEligibilityStatus
{
    PendingReview,
    Eligible,
    Suspended,
    Revoked
}

/// <summary>A registered immutable build and its current admission eligibility.</summary>
public sealed record RegisteredBotBuild(
    BotBuildReference Build,
    BotBuildEligibilityStatus Eligibility);

/// <summary>
/// Host-owned lookup boundary for an exact registered build. Implementations must scope
/// lookup by both bot identity and build ID; admission never silently substitutes another build.
/// </summary>
public interface IRegisteredBotBuildCatalog
{
    ValueTask<RegisteredBotBuild?> FindBuildAsync(
        string botIdentityId,
        string buildId,
        CancellationToken cancellationToken = default);
}
