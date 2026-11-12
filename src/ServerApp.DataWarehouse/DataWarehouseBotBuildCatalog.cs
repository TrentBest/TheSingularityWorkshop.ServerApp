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
public sealed class DataWarehouseBotBuildCatalog : IRegisteredBotBuildCatalog, IDisposable
{
    private readonly object _gate = new();
    private readonly global::TheSingularityWorkshop.DataWarehouse.DataWarehouse _warehouse;
    private readonly DataShelf<BotBuildSlot> _builds;
    private readonly Dictionary<(string BotIdentityId, string BuildId), DataHandle> _index = [];
    private int _disposed;

    public int Capacity => _builds.Capacity;
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
    /// Registers one immutable build. Duplicate identity/build pairs and malformed SHA-256
    /// values are rejected. Eligibility is supplied by the host's review/authorization flow.
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
                    Hash0 = BinaryPrimitives.ReadUInt64LittleEndian(hash.AsSpan(0, 8)),
                    Hash1 = BinaryPrimitives.ReadUInt64LittleEndian(hash.AsSpan(8, 8)),
                    Hash2 = BinaryPrimitives.ReadUInt64LittleEndian(hash.AsSpan(16, 8)),
                    Hash3 = BinaryPrimitives.ReadUInt64LittleEndian(hash.AsSpan(24, 8))
                };
                _index.Add(key, handle);
                error = null;
                return true;
            }
            catch
            {
                _builds.TryFree(handle);
                throw;
            }
        }
    }

    /// <summary>Changes only the mutable eligibility field; build identity and hash stay pinned.</summary>
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
            ref var slot = ref _builds.GetRef(handle);
            slot.Eligibility = (int)eligibility;
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
    /// Copies domain registrations out of the hot shelf for a host-owned durable snapshot.
    /// No DataHandle or native address is exposed as a durable identity.
    /// </summary>
    public IReadOnlyList<RegisteredBotBuild> CreateSnapshot()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var snapshot = new List<RegisteredBotBuild>(_index.Count);
            foreach (var entry in _index.OrderBy(item => item.Key.BotIdentityId, StringComparer.Ordinal)
                         .ThenBy(item => item.Key.BuildId, StringComparer.Ordinal))
            {
                if (!_builds.IsAlive(entry.Value))
                    continue;
                ref readonly var slot = ref _builds.Read(entry.Value);
                Span<byte> hash = stackalloc byte[32];
                BinaryPrimitives.WriteUInt64LittleEndian(hash[..8], slot.Hash0);
                BinaryPrimitives.WriteUInt64LittleEndian(hash.Slice(8, 8), slot.Hash1);
                BinaryPrimitives.WriteUInt64LittleEndian(hash.Slice(16, 8), slot.Hash2);
                BinaryPrimitives.WriteUInt64LittleEndian(hash.Slice(24, 8), slot.Hash3);
                snapshot.Add(new RegisteredBotBuild(
                    new BotBuildReference(entry.Key.BotIdentityId, entry.Key.BuildId, Convert.ToHexString(hash)),
                    (BotBuildEligibilityStatus)slot.Eligibility));
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
            _warehouse.Dispose();
        }
    }
}
