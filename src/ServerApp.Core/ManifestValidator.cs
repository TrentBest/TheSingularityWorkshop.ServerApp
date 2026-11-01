namespace ServerApp.Core;

public sealed record ManifestValidationError(string Code, string Message);

public static class ManifestValidator
{
    public static IReadOnlyList<ManifestValidationError> Validate(ServerRoleManifest? manifest)
    {
        var errors = new List<ManifestValidationError>();
        if (manifest is null)
        {
            errors.Add(new("manifest.required", "A server-role manifest is required."));
            return errors;
        }

        if (manifest.SchemaVersion != 1)
            errors.Add(new("manifest.schema.unsupported", $"Schema version {manifest.SchemaVersion} is not supported; expected 1."));

        if (string.IsNullOrWhiteSpace(manifest.InstanceId))
            errors.Add(new("manifest.instance.required", "InstanceId must be provided."));
        else if (manifest.InstanceId.Length > 128 || manifest.InstanceId.Any(char.IsControl))
            errors.Add(new("manifest.instance.invalid", "InstanceId must be at most 128 characters and contain no control characters."));

        if (string.IsNullOrWhiteSpace(manifest.RoleId))
            errors.Add(new("manifest.role.required", "RoleId must be provided."));
        else if (manifest.RoleId.Length > 128 || manifest.RoleId.Any(char.IsControl))
            errors.Add(new("manifest.role.invalid", "RoleId must be at most 128 characters and contain no control characters."));

        if (manifest.Dependencies is null)
            errors.Add(new("manifest.dependencies.required", "Dependencies cannot be null."));
        else
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dependency in manifest.Dependencies)
            {
                if (string.IsNullOrWhiteSpace(dependency) || dependency.Length > 128 || dependency.Any(char.IsControl))
                    errors.Add(new("manifest.dependency.invalid", "Each dependency must be a non-empty identifier of at most 128 characters."));
                else if (!seen.Add(dependency))
                    errors.Add(new("manifest.dependency.duplicate", $"Dependency '{dependency}' is declared more than once."));
            }
        }

        if (manifest.Resources is null)
            errors.Add(new("manifest.resources.required", "Resource requirements must be provided."));
        else
        {
            if (manifest.Resources.CpuCores < 1 || manifest.Resources.CpuCores > 1024)
                errors.Add(new("manifest.resources.cpu", "CpuCores must be between 1 and 1024."));
            if (manifest.Resources.MemoryMiB < 64 || manifest.Resources.MemoryMiB > 1_048_576)
                errors.Add(new("manifest.resources.memory", "MemoryMiB must be between 64 and 1048576."));
        }

        return errors;
    }
}
