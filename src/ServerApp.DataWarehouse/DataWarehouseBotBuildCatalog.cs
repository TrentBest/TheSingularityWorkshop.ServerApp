using System.Buffers.Binary;
using TheSingularityWorkshop.DataWarehouse;
using ServerApp.Core;

namespace ServerApp.DataWarehouse;

/// <summary>
/// A compact unmanaged row stored in a DataWarehouse shelf. Variable-length domain
/// identifiers remain in the lookup index; the fixed record owns eligibility and SHA-256.
/// </summary>
internal struct BotBuildSlot
{
    public int Eligibility;
    public long Revision;
    public ulong Hash0;
    public ulong Hash1;
    public ulong Hash2;
    public ulong Hash3;
}

/// <summary>
/// Process-local bot-build catalogue backed by DataWarehouse fixed-record storage.
/// All shelf access is externally serialized; no DataShelf ref or native address escapes.
/// </summary>
/// <remarks>
/// This is a hot runtime catalogue, not durable persistence. Callers must persist registration
/// and eligibility changes through a separate durable store and rebuild this catalogue on startup.
/// </remarks>
public sealed class BotBuildCatalogSnapshot
{
    internal Guid OwnerId { get; }
    internal IReadOnlyList<(DataHandle Handle, long Revision)> Entries { get; }
    public IReadOnlyList<RegisteredBotBuild> Registrations { get; }

    internal BotBuildCatalogSnapshot(
        Guid ownerId,
        IReadOnlyList<RegisteredBotBuild> registrations,
        IReadOnlyList<(DataHandle Handle, long Revision)> entries)
    {
        OwnerId = ownerId;
        Registrations = registrations;
        Entries = entries;
    }
}

public sealed class DataWarehouseBotBuildCatalog : IRegisteredBotBuildCatalog, IDisposable
{
    private readonly object _gate = new();
    private readonly Guid _catalogId = Guid.NewGuid();
    private readonly global::TheSingularityWorkshop.DataWarehouse.DataWarehouse _warehouse;
    private readonly DataShelf<BotBuildSlot> _builds;
    private readonly Dictionary<(string BotIdentityId, string BuildId), DataHandle> _index = [];
    private readonly Dictionary<int, (string BotIdentityId, string BuildId)> _keysBySlot = [];
    private int _disposed;

    public int Capacity => _builds.Capacity;
    public int DirtyBuildCount
    {
        get { lock (_gate) { ThrowIfDisposed(); return _builds.DirtyCount; } }
    }
    public int RegisteredBuildCount
    {
        get { lock (_gate) { ThrowIfDisposed(); return _index.Count; } }
    }

    public DataWarehouseBotBuildCatalog(int capacity, int pageSize = 4096)
    {
        _warehouse = new global::TheSingularityWorkshop.DataWarehouse.DataWarehouse(pageSize);
        _builds = _warehouse.CreateShelf<BotBuildSlot>(capacity);
    }

    /// <summary>
    /// Low-level process-local shelf mutation for tests, snapshot restoration, and explicitly
    /// ephemeral catalogues. Authoritative host registration must go through DurableBotBuildRegistry
    /// so the host store commits before the new state becomes visible. Duplicate identity/build
    /// pairs and malformed SHA-256 values are rejected. Eligibility is supplied by host policy.
    /// </summary>
    public bool TryRegister(
        BotBuildReference build,
        BotBuildEligibilityStatus eligibility,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (string.IsNullOrWhiteSpace(build.BotIdentityId) || string.IsNullOrWhiteSpace(build.BuildId))
        {
            error = "Bot identity and build IDs must be non-empty.";
            return false;
        }
        if (!Enum.IsDefined(eligibility))
        {
            error = "The build eligibility status is not recognized.";
            return false;
        }

        byte[] hash;
        try
        {
            if (build.ContentSha256 is null || build.ContentSha256.Length != 64)
                throw new FormatException();
            hash = Convert.FromHexString(build.ContentSha256);
        }
        catch (FormatException)
        {
            error = "The build content hash must be a 64-character SHA-256 hexadecimal value.";
            return false;
        }

        lock (_gate)
        {
            ThrowIfDisposed();
            var key = (build.BotIdentityId, build.BuildId);
            if (_index.ContainsKey(key))
            {
                error = "The bot identity/build pair is already registered.";
                return false;
            }
            if (!_builds.TryAllocate(out var handle))
            {
                error = "The DataWarehouse build shelf is at capacity.";
                return false;
            }

            try
            {
                ref var slot = ref _builds.GetRef(handle);
                slot = new BotBuildSlot
                {
                    Eligibility = (int)eligibility,
                    Revision = 1,
                    Hash0 = BinaryPrimitives.ReadUInt64LittleEndian(hash.AsSpan(0, 8)),
                    Hash1 = BinaryPrimitives.ReadUInt64LittleEndian(hash.AsSpan(8, 8)),
                    Hash2 = BinaryPrimitives.ReadUInt64LittleEndian(hash.AsSpan(16, 8)),
                    Hash3 = BinaryPrimitives.ReadUInt64LittleEndian(hash.AsSpan(24, 8))
                };
                _index.Add(key, handle);
                _keysBySlot.Add(handle.Slot, key);
                error = null;
                return true;
            }
            catch
            {
                _index.Remove(key);
                _keysBySlot.Remove(handle.Slot);
                _builds.TryFree(handle);
                throw;
            }
        }
    }

    /// <summary>
    /// Low-level process-local mutation only. Authoritative eligibility changes must go through
    /// DurableBotBuildRegistry so the host store commits before the new state becomes visible.
    /// Build identity and hash remain pinned.
    /// </summary>
    public bool TrySetEligibility(
        string botIdentityId,
        string buildId,
        BotBuildEligibilityStatus eligibility)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botIdentityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);
        if (!Enum.IsDefined(eligibility))
            throw new ArgumentOutOfRangeException(nameof(eligibility));

        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_index.TryGetValue((botIdentityId, buildId), out var handle) || !_builds.IsAlive(handle))
                return false;
            ref readonly var current = ref _builds.Read(handle);
            if (current.Eligibility == (int)eligibility)
                return true;

            var nextRevision = checked(current.Revision + 1);
            ref var slot = ref _builds.GetRef(handle);
            slot.Eligibility = (int)eligibility;
            slot.Revision = nextRevision;
            return true;
        }
    }

    public ValueTask<RegisteredBotBuild?> FindBuildAsync(
        string botIdentityId,
        string buildId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(botIdentityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(buildId);

        lock (_gate)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (!_index.TryGetValue((botIdentityId, buildId), out var handle) || !_builds.IsAlive(handle))
                return ValueTask.FromResult<RegisteredBotBuild?>(null);

            ref readonly var slot = ref _builds.Read(handle);
            Span<byte> hash = stackalloc byte[32];
            BinaryPrimitives.WriteUInt64LittleEndian(hash[..8], slot.Hash0);
            BinaryPrimitives.WriteUInt64LittleEndian(hash.Slice(8, 8), slot.Hash1);
            BinaryPrimitives.WriteUInt64LittleEndian(hash.Slice(16, 8), slot.Hash2);
            BinaryPrimitives.WriteUInt64LittleEndian(hash.Slice(24, 8), slot.Hash3);

            var build = new BotBuildReference(botIdentityId, buildId, Convert.ToHexString(hash));
            var registration = new RegisteredBotBuild(build, (BotBuildEligibilityStatus)slot.Eligibility);
            return ValueTask.FromResult<RegisteredBotBuild?>(registration);
        }
    }

    /// <summary>
    /// Rehydrates a process-local catalogue from a host-owned durable snapshot.
    /// The supplied records remain the durable source; the returned shelves are only a hot index.
    /// </summary>
    public static DataWarehouseBotBuildCatalog RestoreSnapshot(
        IEnumerable<RegisteredBotBuild> registrations,
        int capacity,
        int pageSize = 4096)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var snapshot = registrations.ToArray();
        if (snapshot.Length > capacity)
            throw new InvalidDataException("The durable snapshot exceeds the configured DataWarehouse shelf capacity.");

        var catalog = new DataWarehouseBotBuildCatalog(capacity, pageSize);
        try
        {
            foreach (var registration in snapshot)
            {
                if (registration is null || registration.Build is null)
                    throw new InvalidDataException("The durable bot-build snapshot contains a null record.");

                if (!catalog.TryRegister(registration.Build, registration.Eligibility, out var error))
                    throw new InvalidDataException(error ?? "The durable bot-build snapshot contains an invalid record.");
            }
            catalog.AcknowledgePersisted(catalog.CaptureDirtySnapshot());
            return catalog;
        }
        catch
        {
            catalog.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Captures only records whose DataWarehouse dirty bit is set. Persist the returned domain
    /// registrations, then acknowledge this exact snapshot after the durable commit succeeds.
    /// </summary>
    public BotBuildCatalogSnapshot CaptureDirtySnapshot()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var dirtySlots = new int[_builds.DirtyCount];
            var dirtyCount = _builds.CopyDirtySlots(dirtySlots);
            var registrations = new List<RegisteredBotBuild>(dirtyCount);
            var entries = new List<(DataHandle Handle, long Revision)>(dirtyCount);
            for (var i = 0; i < dirtyCount; i++)
            {
                var slotIndex = dirtySlots[i];
                if (!_keysBySlot.TryGetValue(slotIndex, out var key) ||
                    !_index.TryGetValue(key, out var handle) ||
                    handle.Slot != slotIndex || !_builds.IsAlive(handle))
                    throw new InvalidOperationException("DataWarehouse dirty tracking and the catalogue index disagree.");

                ref readonly var slot = ref _builds.Read(handle);
                registrations.Add(CreateRegistration(key.BotIdentityId, key.BuildId, slot));
                entries.Add((handle, slot.Revision));
            }

            return new BotBuildCatalogSnapshot(_catalogId, registrations.AsReadOnly(), entries.AsReadOnly());
        }
    }

    /// <summary>
    /// Clears dirty bits only for records unchanged since the captured snapshot. If a record
    /// changed while its snapshot was being persisted, its newer revision remains dirty.
    /// </summary>
    public void AcknowledgePersisted(BotBuildCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (snapshot.OwnerId != _catalogId)
                throw new ArgumentException("The snapshot belongs to another bot-build catalogue.", nameof(snapshot));

            foreach (var entry in snapshot.Entries)
            {
                if (!_builds.IsAlive(entry.Handle))
                    continue;
                ref readonly var current = ref _builds.Read(entry.Handle);
                if (current.Revision == entry.Revision)
                    _builds.MarkClean(entry.Handle);
            }
        }
    }

    private static RegisteredBotBuild CreateRegistration(
        string botIdentityId,
        string buildId,
        in BotBuildSlot slot)
    {
        Span<byte> hash = stackalloc byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(hash[..8], slot.Hash0);
        BinaryPrimitives.WriteUInt64LittleEndian(hash.Slice(8, 8), slot.Hash1);
        BinaryPrimitives.WriteUInt64LittleEndian(hash.Slice(16, 8), slot.Hash2);
        BinaryPrimitives.WriteUInt64LittleEndian(hash.Slice(24, 8), slot.Hash3);
        return new RegisteredBotBuild(
            new BotBuildReference(botIdentityId, buildId, Convert.ToHexString(hash)),
            (BotBuildEligibilityStatus)slot.Eligibility);
    }

    /// <summary>
    /// Copies domain registrations out of the hot shelf for a host-owned durable snapshot.
    /// No DataHandle or native address is exposed as a durable identity.
    /// </summary>
    public IReadOnlyList<RegisteredBotBuild> CreateSnapshot()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var snapshot = new List<RegisteredBotBuild>(_index.Count);
            Span<byte> hash = stackalloc byte[32];
            foreach (var entry in _index.OrderBy(item => item.Key.BotIdentityId, StringComparer.Ordinal)
                         .ThenBy(item => item.Key.BuildId, StringComparer.Ordinal))
            {
                if (!_builds.IsAlive(entry.Value))
                    continue;
                ref readonly var slot = ref _builds.Read(entry.Value);
                snapshot.Add(CreateRegistration(entry.Key.BotIdentityId, entry.Key.BuildId, slot));
            }
            return snapshot.AsReadOnly();
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    public void Dispose()
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _index.Clear();
            _keysBySlot.Clear();
            _warehouse.Dispose();
        }
    }
}
