namespace ServerApp.Core;

public sealed record BatchProvisioningResult(
    bool PlanAccepted,
    IReadOnlyList<ProvisioningResult> Instances,
    IReadOnlyList<ManifestValidationError> PlanningErrors)
{
    public bool IsSuccessful => PlanAccepted &&
        Instances.All(instance => instance.State == ServerInstanceState.Ready);
}

/// <summary>
/// Activates a closed manifest batch in deterministic dependency order.
/// Independent branches continue after a failure; dependents of failed instances are skipped.
/// This is sequential, in-process orchestration, not distributed scheduling.
/// </summary>
public sealed class BatchProvisioningCoordinator
{
    private readonly ProvisioningCoordinator _coordinator;

    public BatchProvisioningCoordinator(ProvisioningCoordinator coordinator) =>
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public async ValueTask<BatchProvisioningResult> ProvisionAsync(
        IEnumerable<ServerRoleManifest> manifests,
        CancellationToken cancellationToken = default)
    {
        var plan = ProvisioningPlanBuilder.Build(manifests);
        if (!plan.IsValid)
            return new(false, Array.Empty<ProvisioningResult>(), plan.Errors);

        var outcomes = new List<ProvisioningResult>(plan.OrderedManifests.Count);
        var unavailable = new HashSet<string>(StringComparer.Ordinal);

        foreach (var manifest in plan.OrderedManifests)
        {
            var failedDependencies = manifest.Dependencies
                .Where(unavailable.Contains)
                .ToArray();

            if (failedDependencies.Length > 0)
            {
                unavailable.Add(manifest.InstanceId);
                outcomes.Add(new(manifest.InstanceId, manifest.RoleId,
                    ServerInstanceState.Skipped,
                    $"Skipped because dependencies did not become Ready: {string.Join(", ", failedDependencies)}.",
                    Array.Empty<ManifestValidationError>()));
                continue;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                unavailable.Add(manifest.InstanceId);
                outcomes.Add(new(manifest.InstanceId, manifest.RoleId,
                    ServerInstanceState.Cancelled, "Batch activation was cancelled.",
                    Array.Empty<ManifestValidationError>()));
                continue;
            }

            try
            {
                var result = await _coordinator.ProvisionAsync(manifest, cancellationToken)
                    .ConfigureAwait(false);
                outcomes.Add(result);
                if (!result.IsReady)
                    unavailable.Add(manifest.InstanceId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                unavailable.Add(manifest.InstanceId);
                outcomes.Add(new(manifest.InstanceId, manifest.RoleId,
                    ServerInstanceState.Cancelled, "Activation was cancelled.",
                    Array.Empty<ManifestValidationError>()));
            }
        }

        return new(true, outcomes, Array.Empty<ManifestValidationError>());
    }
}
