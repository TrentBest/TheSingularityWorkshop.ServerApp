namespace ServerApp.Core;

public sealed record ProvisioningPlanResult(
    IReadOnlyList<ServerRoleManifest> OrderedManifests,
    IReadOnlyList<ManifestValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Builds a deterministic activation order for a closed batch of server-role manifests.
/// Dependencies may refer to another manifest in the batch or to an instance explicitly
/// supplied as already Ready. This planner does not start adapters or reserve resources.
/// </summary>
public static class ProvisioningPlanBuilder
{
    public static ProvisioningPlanResult Build(
        IEnumerable<ServerRoleManifest> manifests,
        IEnumerable<string>? alreadyReadyInstanceIds = null)
    {
        ArgumentNullException.ThrowIfNull(manifests);

        var batch = manifests.ToArray();
        var errors = new List<ManifestValidationError>();
        var ready = new HashSet<string>(
            alreadyReadyInstanceIds ?? Array.Empty<string>(), StringComparer.Ordinal);

        foreach (var manifest in batch)
            errors.AddRange(ManifestValidator.Validate(manifest));

        var byId = new Dictionary<string, ServerRoleManifest>(StringComparer.Ordinal);
        foreach (var manifest in batch)
        {
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.InstanceId))
                continue;

            if (!byId.TryAdd(manifest.InstanceId, manifest))
                errors.Add(new("plan.instance.duplicate",
                    $"InstanceId '{manifest.InstanceId}' appears more than once in the batch."));
        }

        foreach (var manifest in batch)
        {
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.InstanceId) || manifest.Dependencies is null)
                continue;

            foreach (var dependency in manifest.Dependencies)
            {
                if (string.IsNullOrWhiteSpace(dependency))
                    continue;

                if (string.Equals(dependency, manifest.InstanceId, StringComparison.Ordinal))
                    errors.Add(new("plan.dependency.self",
                        $"Instance '{manifest.InstanceId}' cannot depend on itself."));
                else if (!byId.ContainsKey(dependency) && !ready.Contains(dependency))
                    errors.Add(new("plan.dependency.missing",
                        $"Instance '{manifest.InstanceId}' requires '{dependency}', which is neither in the batch nor already Ready."));
            }
        }

        if (errors.Count > 0)
            return new(Array.Empty<ServerRoleManifest>(), errors);

        var indegree = byId.Keys.ToDictionary(id => id, _ => 0, StringComparer.Ordinal);
        var dependents = byId.Keys.ToDictionary(
            id => id, _ => new List<string>(), StringComparer.Ordinal);

        foreach (var manifest in batch)
        {
            foreach (var dependency in manifest.Dependencies)
            {
                if (!byId.ContainsKey(dependency))
                    continue;

                indegree[manifest.InstanceId]++;
                dependents[dependency].Add(manifest.InstanceId);
            }
        }

        var readyQueue = new SortedSet<string>(
            indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key),
            StringComparer.Ordinal);
        var ordered = new List<ServerRoleManifest>(batch.Length);

        while (readyQueue.Count > 0)
        {
            var next = readyQueue.Min!;
            readyQueue.Remove(next);
            ordered.Add(byId[next]);

            foreach (var dependent in dependents[next].OrderBy(id => id, StringComparer.Ordinal))
            {
                indegree[dependent]--;
                if (indegree[dependent] == 0)
                    readyQueue.Add(dependent);
            }
        }

        if (ordered.Count != batch.Length)
        {
            var cyclic = indegree.Where(pair => pair.Value > 0)
                .Select(pair => pair.Key)
                .OrderBy(id => id, StringComparer.Ordinal);
            errors.Add(new("plan.dependency.cycle",
                $"Dependency cycle detected among: {string.Join(", ", cyclic)}."));
            return new(Array.Empty<ServerRoleManifest>(), errors);
        }

        return new(ordered, errors);
    }
}
