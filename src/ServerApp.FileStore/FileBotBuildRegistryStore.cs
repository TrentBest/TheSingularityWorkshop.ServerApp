using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ServerApp.Core;

namespace ServerApp.FileStore;

/// <summary>
/// Database-free, cross-process durable store for the host-owned bot-build registry.
/// DataWarehouse remains a reconstructible in-memory index; this journal is authoritative.
/// </summary>
/// <remarks>
/// Each committed revision is one checksummed append-only frame containing only changed
/// registrations and removals. A complete frame is flushed to stable storage before
/// CommitAsync returns Committed. An incomplete final frame is treated as a torn append
/// and truncated during recovery; a complete malformed frame fails closed.
/// </remarks>
public sealed class FileBotBuildRegistryStore : IBotBuildRegistryStore
{
    private static readonly byte[] CheckpointMagic = Encoding.ASCII.GetBytes("SBRCP001");
    private static readonly byte[] JournalMagic = Encoding.ASCII.GetBytes("SBRJ0001");
    private const int ChecksumLength = 32;
    private const int MaximumFrameLength = 64 * 1024 * 1024;
    private readonly string _directory;
    private readonly string _checkpointPath;
    private readonly string _journalPath;
    private readonly string _lockPath;

    public FileBotBuildRegistryStore(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        _directory = Path.GetFullPath(directoryPath);
        _checkpointPath = Path.Combine(_directory, "bot-build-registry.checkpoint");
        _journalPath = Path.Combine(_directory, "bot-build-registry.journal");
        _lockPath = Path.Combine(_directory, "bot-build-registry.lock");
    }

    public async ValueTask<BotBuildRegistrySnapshot> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        await using var fileLock = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        return RecoverAndLoad(repairTornTail: true);
    }

    public async ValueTask<BotBuildRegistryCommitResult> CommitAsync(
        long expectedRevision,
        BotBuildRegistrySnapshot replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (expectedRevision < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedRevision));
        if (replacement.SchemaVersion != BotBuildRegistrySnapshot.CurrentSchemaVersion)
            throw new NotSupportedException($"Registry schema {replacement.SchemaVersion} is not supported.");
        if (replacement.Revision != checked(expectedRevision + 1))
            throw new ArgumentException("A committed replacement must advance the store revision exactly once.", nameof(replacement));

        Directory.CreateDirectory(_directory);
        await using var fileLock = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var current = RecoverAndLoad(repairTornTail: true);
        if (current.Revision != expectedRevision)
            return BotBuildRegistryCommitResult.RevisionConflict;

        var before = ToDictionary(current.Registrations);
        var after = ToDictionary(replacement.Registrations);
        var upserts = after.Values
            .Where(next => !before.TryGetValue(Key(next), out var previous) || previous != next)
            .OrderBy(item => item.Build.BotIdentityId, StringComparer.Ordinal)
            .ThenBy(item => item.Build.BuildId, StringComparer.Ordinal)
            .ToArray();
        var removals = before.Keys
            .Where(key => !after.ContainsKey(key))
            .OrderBy(key => key.BotIdentityId, StringComparer.Ordinal)
            .ThenBy(key => key.BuildId, StringComparer.Ordinal)
            .ToArray();

        var frame = EncodeJournalFrame(replacement.Revision, upserts, removals);
        cancellationToken.ThrowIfCancellationRequested();
        await using (var stream = new FileStream(
            _journalPath, FileMode.Append, FileAccess.Write, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        // Nothing that can fail is performed after the durable flush and before returning.
        return BotBuildRegistryCommitResult.Committed;
    }

    /// <summary>
    /// Writes a compact full checkpoint, atomically installs it, then truncates the journal.
    /// A crash between installation and truncation is safe: recovery validates and skips
    /// journal revisions already represented by the checkpoint.
    /// </summary>
    public async ValueTask CompactAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        await using var fileLock = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = RecoverAndLoad(repairTornTail: true);
        var checkpoint = EncodeCheckpoint(snapshot);
        var temporaryPath = _checkpointPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(checkpoint, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, _checkpointPath, overwrite: true);

            // The installed checkpoint is already durable before the old journal is removed.
            await using var journal = new FileStream(
                _journalPath, FileMode.Create, FileAccess.Write, FileShare.Read,
                bufferSize: 1, FileOptions.WriteThrough);
            journal.Flush(flushToDisk: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private BotBuildRegistrySnapshot RecoverAndLoad(bool repairTornTail)
    {
        var snapshot = File.Exists(_checkpointPath)
            ? DecodeCheckpoint(File.ReadAllBytes(_checkpointPath))
            : EmptySnapshot();
        if (!File.Exists(_journalPath))
            return snapshot;

        var bytes = File.ReadAllBytes(_journalPath);
        var offset = 0;
        var registrations = ToDictionary(snapshot.Registrations);
        var revision = snapshot.Revision;

        while (offset < bytes.Length)
        {
            var remaining = bytes.Length - offset;
            if (remaining < 24)
            {
                RepairTail(repairTornTail, offset);
                break;
            }

            var span = bytes.AsSpan(offset);
            if (!span[..8].SequenceEqual(JournalMagic))
                throw new InvalidDataException($"Journal framing is invalid at byte offset {offset}.");

            var schemaVersion = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(8, 4));
            var frameRevision = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(12, 8));
            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(20, 4));
            if (payloadLength < 0 || payloadLength > MaximumFrameLength)
                throw new InvalidDataException($"Journal frame length is invalid at byte offset {offset}.");

            var frameLength = checked(24L + payloadLength + ChecksumLength);
            if (frameLength > remaining)
            {
                RepairTail(repairTornTail, offset);
                break;
            }

            if (schemaVersion != BotBuildRegistrySnapshot.CurrentSchemaVersion)
                throw new NotSupportedException($"Journal schema version {schemaVersion} is not supported.");

            var frame = span[..(int)frameLength];
            var expectedChecksum = SHA256.HashData(frame[..(24 + payloadLength)]);
            if (!CryptographicOperations.FixedTimeEquals(
                    expectedChecksum, frame.Slice(24 + payloadLength, ChecksumLength)))
                throw new InvalidDataException($"Journal checksum failed at byte offset {offset}.");

            if (frameRevision > revision)
            {
                if (frameRevision != checked(revision + 1))
                    throw new InvalidDataException($"Journal revision gap: expected {revision + 1}, found {frameRevision}.");
                ApplyJournalPayload(frame.Slice(24, payloadLength), registrations);
                revision = frameRevision;
            }
            // Valid older frames are expected after a checkpoint is installed but before
            // journal truncation completes. Their checksums and schemas are still verified.

            offset += (int)frameLength;
        }

        return new BotBuildRegistrySnapshot(
            revision, registrations.Values.OrderBy(item => item.Build.BotIdentityId, StringComparer.Ordinal)
                .ThenBy(item => item.Build.BuildId, StringComparer.Ordinal).ToArray(),
            BotBuildRegistrySnapshot.CurrentSchemaVersion);
    }

    private void RepairTail(bool repairTornTail, long validLength)
    {
        if (!repairTornTail)
            throw new InvalidDataException("The journal ends with an incomplete frame.");
        using var stream = new FileStream(_journalPath, FileMode.Open, FileAccess.Write, FileShare.Read);
        stream.SetLength(validLength);
        stream.Flush(flushToDisk: true);
    }

    private static byte[] EncodeJournalFrame(
        long revision,
        IReadOnlyList<RegisteredBotBuild> upserts,
        IReadOnlyList<(string BotIdentityId, string BuildId)> removals)
    {
        using var payloadStream = new MemoryStream();
        using (var writer = new BinaryWriter(payloadStream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(upserts.Count);
            foreach (var item in upserts)
                WriteRegistration(writer, item);
            writer.Write(removals.Count);
            foreach (var key in removals)
            {
                WriteString(writer, key.BotIdentityId);
                WriteString(writer, key.BuildId);
            }
        }

        var payload = payloadStream.ToArray();
        var frame = new byte[checked(24 + payload.Length + ChecksumLength)];
        JournalMagic.CopyTo(frame, 0);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8, 4), BotBuildRegistrySnapshot.CurrentSchemaVersion);
        BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(12, 8), revision);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(20, 4), payload.Length);
        payload.CopyTo(frame, 24);
        SHA256.HashData(frame.AsSpan(0, 24 + payload.Length))
            .CopyTo(frame.AsSpan(24 + payload.Length, ChecksumLength));
        return frame;
    }

    private static void ApplyJournalPayload(
        ReadOnlySpan<byte> payload,
        IDictionary<(string BotIdentityId, string BuildId), RegisteredBotBuild> registrations)
    {
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        var upsertCount = ReadCount(reader);
        for (var i = 0; i < upsertCount; i++)
        {
            var registration = ReadRegistration(reader);
            registrations[Key(registration)] = registration;
        }

        var removalCount = ReadCount(reader);
        for (var i = 0; i < removalCount; i++)
        {
            var key = (ReadString(reader), ReadString(reader));
            registrations.Remove(key);
        }

        if (stream.Position != stream.Length)
            throw new InvalidDataException("A journal frame contains trailing payload bytes.");
    }

    private static byte[] EncodeCheckpoint(BotBuildRegistrySnapshot snapshot)
    {
        using var payloadStream = new MemoryStream();
        using (var writer = new BinaryWriter(payloadStream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(BotBuildRegistrySnapshot.CurrentSchemaVersion);
            writer.Write(snapshot.Revision);
            writer.Write(snapshot.Registrations.Count);
            foreach (var registration in snapshot.Registrations
                         .OrderBy(item => item.Build.BotIdentityId, StringComparer.Ordinal)
                         .ThenBy(item => item.Build.BuildId, StringComparer.Ordinal))
                WriteRegistration(writer, registration);
        }

        var payload = payloadStream.ToArray();
        var bytes = new byte[checked(CheckpointMagic.Length + payload.Length + ChecksumLength)];
        CheckpointMagic.CopyTo(bytes, 0);
        payload.CopyTo(bytes, CheckpointMagic.Length);
        SHA256.HashData(bytes.AsSpan(0, CheckpointMagic.Length + payload.Length))
            .CopyTo(bytes.AsSpan(CheckpointMagic.Length + payload.Length, ChecksumLength));
        return bytes;
    }

    private static BotBuildRegistrySnapshot DecodeCheckpoint(byte[] bytes)
    {
        if (bytes.Length < CheckpointMagic.Length + 4 + 8 + 4 + ChecksumLength ||
            !bytes.AsSpan(0, CheckpointMagic.Length).SequenceEqual(CheckpointMagic))
            throw new InvalidDataException("The registry checkpoint header is invalid.");

        var payloadLength = bytes.Length - CheckpointMagic.Length - ChecksumLength;
        var expected = SHA256.HashData(bytes.AsSpan(0, CheckpointMagic.Length + payloadLength));
        if (!CryptographicOperations.FixedTimeEquals(
                expected, bytes.AsSpan(CheckpointMagic.Length + payloadLength, ChecksumLength)))
            throw new InvalidDataException("The registry checkpoint checksum is invalid.");

        using var stream = new MemoryStream(
            bytes, CheckpointMagic.Length, payloadLength, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        var schemaVersion = reader.ReadInt32();
        if (schemaVersion != BotBuildRegistrySnapshot.CurrentSchemaVersion)
            throw new NotSupportedException($"Checkpoint schema version {schemaVersion} is not supported.");
        var revision = reader.ReadInt64();
        if (revision < 0)
            throw new InvalidDataException("The checkpoint revision cannot be negative.");
        var count = ReadCount(reader);
        var registrations = new List<RegisteredBotBuild>(count);
        for (var i = 0; i < count; i++)
            registrations.Add(ReadRegistration(reader));
        if (stream.Position != stream.Length)
            throw new InvalidDataException("The checkpoint contains trailing payload bytes.");

        _ = ToDictionary(registrations);
        return new BotBuildRegistrySnapshot(revision, registrations, schemaVersion);
    }

    private static void WriteRegistration(BinaryWriter writer, RegisteredBotBuild registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(registration.Build);
        if (!Enum.IsDefined(registration.Eligibility))
            throw new InvalidDataException("A registration contains an unknown eligibility status.");
        ValidateBuild(registration.Build);
        WriteString(writer, registration.Build.BotIdentityId);
        WriteString(writer, registration.Build.BuildId);
        writer.Write(Convert.FromHexString(registration.Build.ContentSha256));
        writer.Write((int)registration.Eligibility);
    }

    private static RegisteredBotBuild ReadRegistration(BinaryReader reader)
    {
        var identity = ReadString(reader);
        var buildId = ReadString(reader);
        var hashBytes = reader.ReadBytes(32);
        if (hashBytes.Length != 32)
            throw new EndOfStreamException("A build SHA-256 value is truncated.");
        var eligibilityValue = reader.ReadInt32();
        if (!Enum.IsDefined(typeof(BotBuildEligibilityStatus), eligibilityValue))
            throw new InvalidDataException($"Unknown bot-build eligibility value {eligibilityValue}.");
        return new RegisteredBotBuild(
            new BotBuildReference(identity, buildId, Convert.ToHexString(hashBytes)),
            (BotBuildEligibilityStatus)eligibilityValue);
    }

    private static void ValidateBuild(BotBuildReference build)
    {
        if (string.IsNullOrWhiteSpace(build.BotIdentityId) || string.IsNullOrWhiteSpace(build.BuildId))
            throw new InvalidDataException("Bot identity and build IDs must be non-empty.");
        if (build.ContentSha256 is null || build.ContentSha256.Length != 64)
            throw new InvalidDataException("A build hash must be a 64-character SHA-256 value.");
        try { _ = Convert.FromHexString(build.ContentSha256); }
        catch (FormatException exception) { throw new InvalidDataException("A build hash is not hexadecimal.", exception); }
    }

    private static Dictionary<(string BotIdentityId, string BuildId), RegisteredBotBuild> ToDictionary(
        IEnumerable<RegisteredBotBuild> registrations)
    {
        var result = new Dictionary<(string BotIdentityId, string BuildId), RegisteredBotBuild>();
        foreach (var registration in registrations)
        {
            if (registration is null || registration.Build is null)
                throw new InvalidDataException("A registry snapshot contains a null registration.");
            ValidateBuild(registration.Build);
            if (!Enum.IsDefined(registration.Eligibility))
                throw new InvalidDataException("A registry snapshot contains an unknown eligibility status.");
            if (!result.TryAdd(Key(registration), registration))
                throw new InvalidDataException("A registry snapshot contains a duplicate bot identity/build pair.");
        }
        return result;
    }

    private static (string BotIdentityId, string BuildId) Key(RegisteredBotBuild item) =>
        (item.Build.BotIdentityId, item.Build.BuildId);

    private static int ReadCount(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > 1_000_000)
            throw new InvalidDataException($"A registry record count is invalid: {count}.");
        return count;
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length == 0 || bytes.Length > 1_048_576)
            throw new InvalidDataException("A registry identifier has an invalid encoded length.");
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length <= 0 || length > 1_048_576)
            throw new InvalidDataException($"A registry identifier length is invalid: {length}.");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException("A registry identifier is truncated.");
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static BotBuildRegistrySnapshot EmptySnapshot() =>
        new(0, Array.Empty<RegisteredBotBuild>(), BotBuildRegistrySnapshot.CurrentSchemaVersion);

    private async ValueTask<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    _lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
