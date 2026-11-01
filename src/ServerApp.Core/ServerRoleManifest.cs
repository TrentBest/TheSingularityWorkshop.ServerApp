using System.Text.Json;
using System.Text.Json.Serialization;

namespace ServerApp.Core;

/// <summary>
/// Declarative request to provision one server-role instance. This is not an FSM_COS RuntimeManifest.
/// </summary>
public sealed record ServerRoleManifest
{
    public required int SchemaVersion { get; init; }
    public required string InstanceId { get; init; }
    public required string RoleId { get; init; }
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();
    public required ResourceRequirements Resources { get; init; }

    public static ServerRoleManifest Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<ServerRoleManifest>(json, JsonOptions)
            ?? throw new JsonException("The server-role manifest was empty.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}

public sealed record ResourceRequirements
{
    public required int CpuCores { get; init; }
    public required int MemoryMiB { get; init; }
}
