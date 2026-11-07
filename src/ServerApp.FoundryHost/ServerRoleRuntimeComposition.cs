using System.Threading;
using ServerApp.Core;
using TheSingularityWorkshop.FSM_COS;

namespace ServerApp.FoundryHost;

/// <summary>
/// Host-owned allowlist mapping an operational role ID to its approved FSM_COS root bundles.
/// This is deliberately separate from the client/server operational manifest.
/// </summary>
public interface IServerRoleCompositionCatalog
{
    bool TryGetDefinition(string roleId, out ServerRoleCompositionDefinition? definition);
}

/// <summary>An immutable, host-approved set of FSM_COS roots for one server role.</summary>
public sealed class ServerRoleCompositionDefinition
{
    public ServerRoleCompositionDefinition(
        string roleId,
        IEnumerable<MicroBundleManifestEntry> rootBundles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleId);
        ArgumentNullException.ThrowIfNull(rootBundles);

        RoleId = roleId;
        var roots = rootBundles.ToArray();
        if (roots.Length == 0)
            throw new ArgumentException("A composed server role must declare at least one root MicroBundle.", nameof(rootBundles));
        if (roots.Any(root => root.BundleId == 0 || string.IsNullOrWhiteSpace(root.Version)))
            throw new ArgumentException("Every root MicroBundle must have a non-zero ID and version.", nameof(rootBundles));
        if (roots.Select(root => root.BundleId).Distinct().Count() != roots.Length)
            throw new ArgumentException("Root MicroBundle IDs must be unique within a role definition.", nameof(rootBundles));

        RootBundles = Array.AsReadOnly(roots);
    }

    public string RoleId { get; }
    public IReadOnlyList<MicroBundleManifestEntry> RootBundles { get; }
}

/// <summary>Supplies unique, process-local FSM_COS runtime identities for this host.</summary>
public interface IRuntimeIdentityProvider
{
    ulong NextRuntimeId();
}

/// <summary>A simple process-local monotonic runtime identity source.</summary>
public sealed class SequentialRuntimeIdentityProvider : IRuntimeIdentityProvider
{
    private long _lastIssued;

    public SequentialRuntimeIdentityProvider(long initialValue = 0)
    {
        if (initialValue < 0)
            throw new ArgumentOutOfRangeException(nameof(initialValue));
        _lastIssued = initialValue;
    }

    public ulong NextRuntimeId()
    {
        var next = Interlocked.Increment(ref _lastIssued);
        if (next <= 0)
            throw new InvalidOperationException("The process-local runtime identity space is exhausted.");
        return (ulong)next;
    }
}

/// <summary>
/// Resolves approved role roots and creates a fresh composition session for every candidate.
/// A session, not this shared coordinator, owns the candidate's FSM_COS catalog and registry.
/// </summary>
public sealed class ServerRoleRuntimeComposer
{
    private readonly IServerRoleCompositionCatalog _definitions;
    private readonly IServerRoleCompositionSessionFactory _sessions;
    private readonly IRuntimeIdentityProvider _runtimeIds;

    public ServerRoleRuntimeComposer(
        IServerRoleCompositionSessionFactory sessions,
        IServerRoleCompositionCatalog definitions,
        IRuntimeIdentityProvider? runtimeIds = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));
        _runtimeIds = runtimeIds ?? new SequentialRuntimeIdentityProvider();
    }

    public ServerRoleCompositionDefinition ValidateAndResolve(ServerRoleManifest manifest)
    {
        var errors = ManifestValidator.Validate(manifest);
        if (errors.Count > 0)
            throw new InvalidOperationException(
                $"Server-role manifest validation failed: {string.Join("; ", errors.Select(error => error.Code + ": " + error.Message))}");

        if (!_definitions.TryGetDefinition(manifest.RoleId, out var definition) || definition is null)
            throw new InvalidOperationException(
                $"No approved FSM_COS composition definition exists for role '{manifest.RoleId}'.");

        if (!string.Equals(definition.RoleId, manifest.RoleId, StringComparison.Ordinal))
            throw new InvalidOperationException("The role composition catalog returned a definition for a different RoleId.");

        return definition;
    }

    public IServerRoleCompositionSession CreateSession(ServerRoleCompositionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return _sessions.Create(definition)
            ?? throw new InvalidOperationException("The composition session factory returned null.");
    }

    public RuntimeAssembly Compose(
        IServerRoleCompositionSession session,
        ServerRoleCompositionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(definition);
        var manifest = new RuntimeManifest(
            RuntimeId: _runtimeIds.NextRuntimeId(),
            Bundles: definition.RootBundles);

        return session.Compose(manifest);
    }
}

/// <summary>
/// ServerApp host capability that consumes a composed RuntimeAssembly and starts the actual
/// server role. This contract belongs to the host layer, not FSM_COS or ServerApp.Core.
/// </summary>
public interface IServerRoleRuntimeAdapter
{
    string RoleId { get; }

    ValueTask<RoleStartResult> StartAsync(
        ServerRoleManifest manifest,
        RuntimeAssembly assembly,
        CancellationToken cancellationToken);
}
