namespace ServerApp.Core;

public enum ServerInstanceState
{
    Accepted,
    Starting,
    Ready,
    Failed,
    Skipped,
    Cancelled
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
/// Local, in-process role activation. Dependencies name previously provisioned instance IDs
/// that must be Ready in this coordinator. This registry is process-local and is not durable.
/// Only explicitly registered adapters can be invoked.
/// </summary>
public sealed class ProvisioningCoordinator
{
    private readonly IReadOnlyDictionary<string, IServerRoleAdapter> _adapters;
    private readonly object _gate = new();
    private readonly HashSet<string> _readyInstances = new(StringComparer.Ordinal);
    private readonly HashSet<string> _startingInstances = new(StringComparer.Ordinal);

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
            return Failed(manifest, $"No registered adapter supports role '{manifest.RoleId}'.");

        lock (_gate)
        {
            if (_readyInstances.Contains(manifest.InstanceId) || !_startingInstances.Add(manifest.InstanceId))
                return Failed(manifest, $"InstanceId '{manifest.InstanceId}' is already registered or starting.");

            var missing = manifest.Dependencies
                .Where(id => !_readyInstances.Contains(id))
                .ToArray();
            if (missing.Length > 0)
            {
                _startingInstances.Remove(manifest.InstanceId);
                return Failed(manifest,
                    $"Required instance dependencies are not Ready: {string.Join(", ", missing)}.");
            }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = await adapter.StartAsync(manifest, cancellationToken).ConfigureAwait(false);
            if (!start.IsReady)
                return Failed(manifest, start.Detail);

            lock (_gate)
            {
                _startingInstances.Remove(manifest.InstanceId);
                _readyInstances.Add(manifest.InstanceId);
            }

            return new(manifest.InstanceId, manifest.RoleId, ServerInstanceState.Ready,
                start.Detail, Array.Empty<ManifestValidationError>());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failed(manifest,
                $"Role activation failed: {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            lock (_gate)
                _startingInstances.Remove(manifest.InstanceId);
        }
    }

    private static ProvisioningResult Failed(ServerRoleManifest manifest, string detail) =>
        new(manifest.InstanceId, manifest.RoleId, ServerInstanceState.Failed,
            detail, Array.Empty<ManifestValidationError>());
}
