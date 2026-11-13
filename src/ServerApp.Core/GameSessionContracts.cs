using System.Collections.Concurrent;

namespace ServerApp.Core;

/// <summary>Lifecycle state reported by the trusted game-session adapter.</summary>
public enum GameSessionState
{
    Starting,
    Ready,
    Running,
    Completed,
    Failed,
    Aborted
}

/// <summary>
/// Immutable observation of one external game session. Session IDs are host-assigned and
/// adapter versions are pinned by the immutable MatchAssignment.
/// </summary>
public sealed record GameSessionSnapshot(
    string MatchId,
    string SessionId,
    string GameAdapterVersion,
    GameSessionState State,
    DateTimeOffset ObservedAtUtc,
    string? FailureCode = null,
    string? FailureMessage = null);

/// <summary>Host-assigned correlation data supplied to a game-session adapter.</summary>
public sealed record GameSessionStartRequest(
    MatchAssignment Assignment,
    string SessionId);

/// <summary>Result of attempting to start one assigned match.</summary>
public sealed record GameSessionStartResult(
    GameSessionSnapshot? Session,
    IReadOnlyList<CompetitionContractError> Errors)
{
    public bool IsReady => Session?.State == GameSessionState.Ready && Errors.Count == 0;
}

/// <summary>
/// Game-specific integration boundary. Implementations own the external game process/client
/// details and must report Ready only after the game session and every required participant
/// have passed their readiness checks.
/// </summary>
public interface IGameSessionAdapter
{
    string AdapterVersion { get; }

    ValueTask<GameSessionSnapshot> StartAsync(
        GameSessionStartRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<GameSessionSnapshot?> GetStatusAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    ValueTask StopAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Coordinates host-owned session identity and lifecycle around an injected game adapter.
/// The first implementation is process-local; durable match/session records remain a separate
/// host-storage concern. Adapter-reported state is validated before it becomes visible.
/// </summary>
public sealed class GameSessionCoordinator
{
    private sealed class SessionEntry(GameSessionSnapshot initial)
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public GameSessionSnapshot Snapshot = initial;
    }

    private readonly CompetitionDefinition _competition;
    private readonly IGameSessionAdapter _adapter;
    private readonly ConcurrentDictionary<string, SessionEntry> _sessions = new(StringComparer.Ordinal);

    public GameSessionCoordinator(CompetitionDefinition competition, IGameSessionAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(competition);
        ArgumentNullException.ThrowIfNull(adapter);
        var definitionErrors = CompetitionContractValidator.ValidateDefinition(competition);
        if (definitionErrors.Count != 0)
            throw new ArgumentException("The competition definition is invalid.", nameof(competition));
        ArgumentException.ThrowIfNullOrWhiteSpace(adapter.AdapterVersion);

        _competition = competition;
        _adapter = adapter;
    }

    /// <summary>
    /// Starts an already-admitted immutable match assignment. The host reserves the match ID
    /// before calling the adapter and assigns the session ID; duplicate starts are rejected.
    /// </summary>
    public async ValueTask<GameSessionStartResult> StartAsync(
        MatchAssignment assignment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var errors = CompetitionContractValidator.ValidateAssignment(_competition, assignment).ToList();
        if (!string.Equals(assignment.GameAdapterVersion, _adapter.AdapterVersion, StringComparison.Ordinal))
            errors.Add(new("session.adapter.version",
                "The assignment's game adapter version does not match the registered host adapter."));
        if (errors.Count != 0)
            return new(null, Array.AsReadOnly(errors.ToArray()));

        var sessionId = Guid.NewGuid().ToString("N");
        var starting = new GameSessionSnapshot(
            assignment.MatchId, sessionId, _adapter.AdapterVersion,
            GameSessionState.Starting, DateTimeOffset.UtcNow);
        var entry = new SessionEntry(starting);
        if (!_sessions.TryAdd(assignment.MatchId, entry))
            return new(null, [new("session.match.duplicate",
                "A game session has already been started or reserved for this match ID.")]);

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GameSessionSnapshot adapterSnapshot;
            try
            {
                adapterSnapshot = await _adapter.StartAsync(
                    new GameSessionStartRequest(assignment, sessionId), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await TryStopAfterFailedStartAsync(sessionId).ConfigureAwait(false);
                entry.Snapshot = CreateTerminal(starting, GameSessionState.Aborted,
                    "session.start.cancelled", "Game-session startup was cancelled.");
                throw;
            }
            catch (Exception exception)
            {
                entry.Snapshot = CreateTerminal(starting, GameSessionState.Failed,
                    "session.start.exception", exception.Message);
                return Failure(entry.Snapshot, "session.start.exception",
                    "The game-session adapter failed while starting the match.");
            }

            var validationError = ValidateAdapterSnapshot(
                adapterSnapshot, assignment.MatchId, sessionId, _adapter.AdapterVersion);
            if (validationError is not null)
            {
                await TryStopAfterFailedStartAsync(sessionId).ConfigureAwait(false);
                entry.Snapshot = CreateTerminal(starting, GameSessionState.Failed,
                    "session.start.invalid-snapshot", validationError);
                return Failure(entry.Snapshot, "session.start.invalid-snapshot", validationError);
            }

            if (adapterSnapshot.State == GameSessionState.Failed)
            {
                entry.Snapshot = adapterSnapshot;
                return new(adapterSnapshot,
                [
                    new(adapterSnapshot.FailureCode ?? "session.start.failed",
                        adapterSnapshot.FailureMessage ?? "The game-session adapter failed to become ready.")
                ]);
            }

            if (adapterSnapshot.State != GameSessionState.Ready)
            {
                await TryStopAfterFailedStartAsync(sessionId).ConfigureAwait(false);
                entry.Snapshot = CreateTerminal(starting, GameSessionState.Failed,
                    "session.start.not-ready", "The adapter did not establish Ready before returning from StartAsync.");
                return Failure(entry.Snapshot, "session.start.not-ready",
                    "The adapter did not establish Ready before returning from StartAsync.");
            }

            if (adapterSnapshot.FailureCode is not null || adapterSnapshot.FailureMessage is not null)
            {
                await TryStopAfterFailedStartAsync(sessionId).ConfigureAwait(false);
                entry.Snapshot = CreateTerminal(starting, GameSessionState.Failed,
                    "session.start.invalid-ready", "A Ready session cannot contain failure metadata.");
                return Failure(entry.Snapshot, "session.start.invalid-ready",
                    "A Ready session cannot contain failure metadata.");
            }

            entry.Snapshot = adapterSnapshot;
            return new(adapterSnapshot, Array.Empty<CompetitionContractError>());
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>Refreshes one session from the trusted adapter and validates its state transition.</summary>
    public async ValueTask<GameSessionSnapshot?> RefreshAsync(
        string matchId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(matchId);
        if (!_sessions.TryGetValue(matchId, out var entry))
            return null;

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = entry.Snapshot;
            if (IsTerminal(current.State))
                return current;

            GameSessionSnapshot? observed;
            try
            {
                observed = await _adapter.GetStatusAsync(current.SessionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                entry.Snapshot = CreateTerminal(current, GameSessionState.Failed,
                    "session.observe.exception", exception.Message);
                return entry.Snapshot;
            }

            if (observed is null)
                return current;

            var validationError = ValidateAdapterSnapshot(
                observed, current.MatchId, current.SessionId, _adapter.AdapterVersion);
            if (validationError is not null)
            {
                entry.Snapshot = CreateTerminal(current, GameSessionState.Failed,
                    "session.observe.invalid-snapshot", validationError);
                return entry.Snapshot;
            }

            if (!IsAllowedTransition(current.State, observed.State))
            {
                entry.Snapshot = CreateTerminal(current, GameSessionState.Failed,
                    "session.transition.invalid",
                    $"The adapter reported an invalid session transition from {current.State} to {observed.State}.");
                return entry.Snapshot;
            }

            entry.Snapshot = observed;
            return observed;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>Stops a non-terminal session and records Aborted only after adapter stop succeeds.</summary>
    public async ValueTask<GameSessionSnapshot?> StopAsync(
        string matchId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(matchId);
        if (!_sessions.TryGetValue(matchId, out var entry))
            return null;

        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = entry.Snapshot;
            if (IsTerminal(current.State))
                return current;

            try
            {
                await _adapter.StopAsync(current.SessionId, cancellationToken).ConfigureAwait(false);
                entry.Snapshot = CreateTerminal(current, GameSessionState.Aborted, null, null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                entry.Snapshot = CreateTerminal(current, GameSessionState.Failed,
                    "session.stop.exception", exception.Message);
            }

            return entry.Snapshot;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public GameSessionSnapshot? GetSnapshot(string matchId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(matchId);
        return _sessions.TryGetValue(matchId, out var entry) ? entry.Snapshot : null;
    }

    private async ValueTask TryStopAfterFailedStartAsync(string sessionId)
    {
        try { await _adapter.StopAsync(sessionId, CancellationToken.None).ConfigureAwait(false); }
        catch { /* Preserve the primary startup failure; the host records Failed/Aborted. */ }
    }

    private static string? ValidateAdapterSnapshot(
        GameSessionSnapshot? snapshot,
        string matchId,
        string sessionId,
        string adapterVersion)
    {
        if (snapshot is null)
            return "The game-session adapter returned no session snapshot.";
        if (!string.Equals(snapshot.MatchId, matchId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.SessionId, sessionId, StringComparison.Ordinal))
            return "The game-session adapter returned a snapshot for a different match or session.";
        if (!string.Equals(snapshot.GameAdapterVersion, adapterVersion, StringComparison.Ordinal))
            return "The game-session adapter snapshot version does not match the registered adapter.";
        if (snapshot.ObservedAtUtc.Offset != TimeSpan.Zero)
            return "The game-session adapter timestamp must use UTC.";
        if (!Enum.IsDefined(snapshot.State))
            return "The game-session adapter returned an unknown lifecycle state.";
        return null;
    }

    private static bool IsAllowedTransition(GameSessionState current, GameSessionState next) =>
        current == next || current switch
        {
            GameSessionState.Starting => next is GameSessionState.Ready or GameSessionState.Failed or GameSessionState.Aborted,
            GameSessionState.Ready => next is GameSessionState.Running or GameSessionState.Completed or GameSessionState.Failed or GameSessionState.Aborted,
            GameSessionState.Running => next is GameSessionState.Completed or GameSessionState.Failed or GameSessionState.Aborted,
            _ => false
        };

    private static bool IsTerminal(GameSessionState state) =>
        state is GameSessionState.Completed or GameSessionState.Failed or GameSessionState.Aborted;

    private static GameSessionSnapshot CreateTerminal(
        GameSessionSnapshot current,
        GameSessionState state,
        string? failureCode,
        string? failureMessage) =>
        current with
        {
            State = state,
            ObservedAtUtc = DateTimeOffset.UtcNow,
            FailureCode = failureCode,
            FailureMessage = failureMessage
        };

    private static GameSessionStartResult Failure(
        GameSessionSnapshot snapshot,
        string code,
        string message) =>
        new(snapshot, [new CompetitionContractError(code, message)]);
}
