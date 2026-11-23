namespace ServerApp.ExecutionHost;

/// <summary>
/// Optional host-owned observation sink. Implementations must be non-blocking; exceptions
/// are isolated by the provisioning host and never change provisioning outcomes.
/// </summary>
public interface IServerRoleExecutionObserver
{
    void OnObservation(ServerRoleExecutionObservation observation);
}

/// <summary>
/// A factual observation emitted at a ServerApp/Foundry/FSM_COS boundary.
/// Bundle observations describe membership in the returned RuntimeAssembly only.
/// </summary>
public sealed record ServerRoleExecutionObservation(
    Guid RunId,
    long Sequence,
    string Phase,
    string Status,
    DateTimeOffset ObservedAt,
    ulong? RuntimeId = null,
    ulong? BundleId = null,
    string? BundleVersion = null,
    string? Detail = null);
