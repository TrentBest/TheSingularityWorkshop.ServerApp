namespace ServerApp.Simulation;

internal enum SwarmTaskLifecycle
{
    Issued,
    Delivered,
    Completed,
    Failed,
    Expired,
    Unconfirmed
}

/// <summary>
/// Host-owned evidence ledger shared by normal and fault-injected swarm runs.
/// It accepts acknowledgements only when sender, issued task identity, and lifecycle agree.
/// </summary>
internal sealed class SwarmExecutionLedger
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public void Issue(string agentId, string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        if (!_entries.TryAdd(agentId, new Entry(taskId)))
            throw new InvalidOperationException($"Agent '{agentId}' already has an issued task.");
    }

    public void MarkDelivered(string agentId, string taskId)
    {
        var entry = GetExpected(agentId, taskId);
        if (entry.State != SwarmTaskLifecycle.Issued)
            throw new InvalidOperationException($"Task for agent '{agentId}' cannot transition from {entry.State} to Delivered.");
        entry.State = SwarmTaskLifecycle.Delivered;
    }

    public void MarkFailed(string agentId, string taskId)
    {
        var entry = GetExpected(agentId, taskId);
        if (entry.State is not (SwarmTaskLifecycle.Issued or SwarmTaskLifecycle.Delivered))
            throw new InvalidOperationException($"Task for agent '{agentId}' cannot transition from {entry.State} to Failed.");
        entry.State = SwarmTaskLifecycle.Failed;
    }

    public void MarkExpired(string agentId)
    {
        if (!_entries.TryGetValue(agentId, out var entry))
            throw new InvalidOperationException($"Agent '{agentId}' has no issued task.");
        if (entry.State != SwarmTaskLifecycle.Issued)
            throw new InvalidOperationException($"Task for agent '{agentId}' cannot transition from {entry.State} to Expired.");
        entry.State = SwarmTaskLifecycle.Expired;
    }

    public bool TryAcknowledge(string senderAgentId, string taskId)
    {
        var entry = GetExpected(senderAgentId, taskId);
        if (entry.State != SwarmTaskLifecycle.Delivered)
            return false;
        entry.State = SwarmTaskLifecycle.Completed;
        return true;
    }

    public SwarmTaskLifecycle GetState(string agentId) =>
        _entries.TryGetValue(agentId, out var entry)
            ? entry.State
            : SwarmTaskLifecycle.Unconfirmed;

    public string GetTaskId(string agentId) =>
        _entries.TryGetValue(agentId, out var entry) ? entry.TaskId : string.Empty;

    public bool IsAcknowledged(string agentId) =>
        GetState(agentId) == SwarmTaskLifecycle.Completed;

    public int AcknowledgedCount =>
        _entries.Values.Count(entry => entry.State == SwarmTaskLifecycle.Completed);

    public int IssuedCount => _entries.Count;

    private Entry GetExpected(string agentId, string taskId)
    {
        if (!_entries.TryGetValue(agentId, out var entry) ||
            !string.Equals(entry.TaskId, taskId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Task acknowledgement or transition did not match the host-issued task for agent '{agentId}'.");
        return entry;
    }

    private sealed class Entry(string taskId)
    {
        public string TaskId { get; } = taskId;
        public SwarmTaskLifecycle State { get; set; } = SwarmTaskLifecycle.Issued;
    }
}
