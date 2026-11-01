namespace ServerApp.Core;

public sealed record HostCapacityProfile(
    string HostId,
    int CpuCores,
    int MemoryMiB);

public sealed record HostPlacement(
    string InstanceId,
    string HostId,
    int RemainingCpuCores,
    int RemainingMemoryMiB);

public sealed record UnplacedInstance(
    string InstanceId,
    string Reason);

public sealed record HostPlacementPlanResult(
    bool PlanAccepted,
    IReadOnlyList<HostPlacement> Placements,
    IReadOnlyList<UnplacedInstance> UnplacedInstances,
    IReadOnlyList<ManifestValidationError> Errors)
{
    public bool IsFullyPlaced => PlanAccepted && UnplacedInstances.Count == 0;
}

/// <summary>
/// Deterministic first-fit placement against a supplied snapshot of host capacity.
/// Hosts are considered in ordinal HostId order; each accepted placement consumes
/// CPU and memory from the planner's private capacity ledger. No host is mutated
/// and no infrastructure is provisioned.
/// </summary>
public static class HostPlacementPlanner
{
    public static HostPlacementPlanResult Build(
        IEnumerable<ServerRoleManifest> manifests,
        IEnumerable<HostCapacityProfile> hosts,
        IEnumerable<string>? alreadyReadyInstanceIds = null)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(hosts);

        var hostList = hosts.ToArray();
        var errors = new List<ManifestValidationError>();
        var hostIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var host in hostList)
        {
            if (host is null)
            {
                errors.Add(new("placement.host.required", "Host profiles cannot contain null entries."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(host.HostId) || host.HostId.Length > 128 ||
                host.HostId.Any(char.IsControl))
                errors.Add(new("placement.host.id", "HostId must be a non-empty identifier of at most 128 characters."));
            else if (!hostIds.Add(host.HostId))
                errors.Add(new("placement.host.duplicate", $"HostId '{host.HostId}' appears more than once."));

            if (host.CpuCores < 1 || host.CpuCores > 1_048_576)
                errors.Add(new("placement.host.cpu", $"Host '{host.HostId}' CpuCores must be between 1 and 1048576."));
            if (host.MemoryMiB < 64)
                errors.Add(new("placement.host.memory", $"Host '{host.HostId}' MemoryMiB must be at least 64."));
        }

        var plan = ProvisioningPlanBuilder.Build(manifests, alreadyReadyInstanceIds);
        errors.AddRange(plan.Errors);
        if (errors.Count > 0)
            return new(false, Array.Empty<HostPlacement>(), Array.Empty<UnplacedInstance>(), errors);

        var capacity = hostList
            .OrderBy(host => host.HostId, StringComparer.Ordinal)
            .Select(host => new MutableCapacity(host))
            .ToArray();
        var placements = new List<HostPlacement>();
        var unplaced = new List<UnplacedInstance>();

        foreach (var manifest in plan.OrderedManifests)
        {
            var host = capacity.FirstOrDefault(candidate =>
                candidate.RemainingCpuCores >= manifest.Resources.CpuCores &&
                candidate.RemainingMemoryMiB >= manifest.Resources.MemoryMiB);

            if (host is null)
            {
                unplaced.Add(new(manifest.InstanceId,
                    $"No host has sufficient remaining capacity for {manifest.Resources.CpuCores} CPU core(s) and {manifest.Resources.MemoryMiB} MiB."));
                continue;
            }

            host.RemainingCpuCores -= manifest.Resources.CpuCores;
            host.RemainingMemoryMiB -= manifest.Resources.MemoryMiB;
            placements.Add(new(manifest.InstanceId, host.HostId,
                host.RemainingCpuCores, host.RemainingMemoryMiB));
        }

        return new(true, placements, unplaced, Array.Empty<ManifestValidationError>());
    }

    private sealed class MutableCapacity(HostCapacityProfile profile)
    {
        public string HostId { get; } = profile.HostId;
        public int RemainingCpuCores { get; set; } = profile.CpuCores;
        public int RemainingMemoryMiB { get; set; } = profile.MemoryMiB;
    }
}
