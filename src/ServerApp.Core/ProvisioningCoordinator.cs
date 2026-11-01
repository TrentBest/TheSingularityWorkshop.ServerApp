namespace ServerApp.Core;

public enum ServerInstanceState
{
    Accepted,
    Starting,
    Ready,
    Failed
}

public sealed record RoleStartResult(bool IsReady, string Detail);

public interface IServerRoleAdapter
{
    string RoleId { get; }
    ValueTask<RoleStartResult> StartAsync(ServerRoleManifest manifest, CancellationToken cancellationToken);
}

public sealed record ProvisioningResult(
    string InstanceId,
    string RoleId,
    ServerInstanceState State,
    string Detail,
    IReadOnlyList<ManifestValidationError> ValidationErrors)
{
    public bool IsReady => State == ServerInstanceState.Ready;
}

/// <summary>
/// Local, in-process role activation. It only invokes explicitly registered adapters;
/// manifests cannot name assemblies, executables, shell commands, or arbitrary code.
/// </summary>
public sealed class ProvisioningCoordinator
{
    private readonly IReadOnlyDictionary<string, IServerRoleAdapter> _adapters;

    public ProvisioningCoordinator(IEnumerable<IServerRoleAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        var map = new Dictionary<string, IServerRoleAdapter>(StringComparer.Ordinal);
        foreach (var adapter in adapters)
        {
            ArgumentNullException.ThrowIfNull(adapter);
            if (string.IsNullOrWhiteSpace(adapter.RoleId))
                throw new ArgumentException("Every role adapter must declare a non-empty RoleId.", nameof(adapters));
            if (!map.TryAdd(adapter.RoleId, adapter))
                throw new ArgumentException($"Duplicate role adapter '{adapter.RoleId}'.", nameof(adapters));
        }
        _adapters = map;
    }

    public async ValueTask<ProvisioningResult> ProvisionAsync(
        ServerRoleManifest manifest,
        CancellationToken cancellationToken = default)
    {
        var errors = ManifestValidator.Validate(manifest);
        if (errors.Count > 0)
            return new(manifest?.InstanceId ?? string.Empty, manifest?.RoleId ?? string.Empty,
                ServerInstanceState.Failed, "Manifest validation failed.", errors);

        if (!_adapters.TryGetValue(manifest.RoleId, out var adapter))
            return new(manifest.InstanceId, manifest.RoleId, ServerInstanceState.Failed,
                $"No registered adapter supports role '{manifest.RoleId}'.",
                Array.Empty<ManifestValidationError>());

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = await adapter.StartAsync(manifest, cancellationToken).ConfigureAwait(false);
            return new(manifest.InstanceId, manifest.RoleId,
                start.IsReady ? ServerInstanceState.Ready : ServerInstanceState.Failed,
                start.Detail, Array.Empty<ManifestValidationError>());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new(manifest.InstanceId, manifest.RoleId, ServerInstanceState.Failed,
                $"Role activation failed: {exception.GetType().Name}: {exception.Message}",
                Array.Empty<ManifestValidationError>());
        }
    }
}
