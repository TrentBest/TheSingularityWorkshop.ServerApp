namespace ServerApp.Core;

/// <summary>
/// Host-owned lookup boundary for registered bot builds. Implementations must return a build
/// only when its identity is registered and the build is currently eligible for competition.
/// Null deliberately does not distinguish unknown identities from ineligible builds.
/// </summary>
public interface IEligibleBotBuildCatalog
{
    ValueTask<BotBuildReference?> FindEligibleBuildAsync(
        string botIdentityId,
        CancellationToken cancellationToken = default);
}

/// <summary>A participant request before its immutable build has been resolved.</summary>
public sealed record MatchAdmissionParticipant(
    string ParticipantId,
    string BotIdentityId,
    string BuildId);

/// <summary>Admission request for the first supported competitive vertical slice.</summary>
public sealed record MatchAdmissionRequest(
    string FormatId,
    MatchAdmissionParticipant First,
    MatchAdmissionParticipant Second);

public sealed record MatchAdmissionResult(
    MatchAssignment? Assignment,
    IReadOnlyList<CompetitionContractError> Errors)
{
    public bool IsAdmitted => Assignment is not null && Errors.Count == 0;
}

/// <summary>
/// Resolves two distinct registered bot identities to eligible immutable builds and creates
/// a validated 1v1 assignment. This service does not queue, persist, launch, or rate matches.
/// </summary>
public sealed class MatchAdmissionService
{
    private readonly CompetitionDefinition _competition;
    private readonly IRegisteredBotBuildCatalog _buildCatalog;
    private readonly string _gameAdapterVersion;

    public MatchAdmissionService(
        CompetitionDefinition competition,
        IRegisteredBotBuildCatalog buildCatalog,
        string gameAdapterVersion)
    {
        ArgumentNullException.ThrowIfNull(competition);
        ArgumentNullException.ThrowIfNull(buildCatalog);
        if (CompetitionContractValidator.ValidateDefinition(competition).Count > 0)
            throw new ArgumentException("The competition definition is invalid.", nameof(competition));
        ArgumentException.ThrowIfNullOrWhiteSpace(gameAdapterVersion);

        _competition = competition;
        _buildCatalog = buildCatalog;
        _gameAdapterVersion = gameAdapterVersion;
    }

    public async ValueTask<MatchAdmissionResult> AdmitAsync(
        MatchAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<CompetitionContractError>();

        if (!string.Equals(request.FormatId, "1v1", StringComparison.Ordinal) ||
            !_competition.Formats.Any(format =>
                string.Equals(format.FormatId, request.FormatId, StringComparison.Ordinal)))
            errors.Add(new("admission.format.unsupported",
                "The initial admission service supports only a competition-declared 1v1 format."));

        ValidateParticipant(request.First, "first", errors);
        ValidateParticipant(request.Second, "second", errors);

        if (request.First is not null && request.Second is not null)
        {
            if (string.Equals(request.First.ParticipantId, request.Second.ParticipantId, StringComparison.Ordinal))
                errors.Add(new("admission.participant.duplicate", "The two participant IDs must be distinct."));
            if (string.Equals(request.First.BotIdentityId, request.Second.BotIdentityId, StringComparison.Ordinal))
                errors.Add(new("admission.bot-identity.duplicate", "A 1v1 match requires two distinct bot identities."));
        }

        if (errors.Count > 0)
            return new(null, Array.AsReadOnly(errors.ToArray()));

        cancellationToken.ThrowIfCancellationRequested();
        var firstRegistration = await _buildCatalog.FindBuildAsync(
            request.First.BotIdentityId, request.First.BuildId, cancellationToken).ConfigureAwait(false);
        var secondRegistration = await _buildCatalog.FindBuildAsync(
            request.Second.BotIdentityId, request.Second.BuildId, cancellationToken).ConfigureAwait(false);
        var firstBuild = firstRegistration?.Build;
        var secondBuild = secondRegistration?.Build;

        ValidateBuildRegistration(firstRegistration, request.First, "first", errors);
        ValidateBuildRegistration(secondRegistration, request.Second, "second", errors);

        if (errors.Count > 0)
            return new(null, Array.AsReadOnly(errors.ToArray()));

        var assignment = new MatchAssignment(
            Guid.NewGuid().ToString("N"),
            _competition.CompetitionId,
            _competition.GameId,
            _competition.RulesetId,
            _competition.RulesetVersion,
            request.FormatId,
            _gameAdapterVersion,
            DateTimeOffset.UtcNow,
            [
                new MatchParticipantAssignment(request.First.ParticipantId, "A", firstBuild!),
                new MatchParticipantAssignment(request.Second.ParticipantId, "B", secondBuild!)
            ]);

        errors.AddRange(CompetitionContractValidator.ValidateAssignment(_competition, assignment));
        return errors.Count == 0
            ? new(assignment, Array.Empty<CompetitionContractError>())
            : new(null, Array.AsReadOnly(errors.ToArray()));
    }

    private static void ValidateBuildRegistration(
        RegisteredBotBuild? registration,
        MatchAdmissionParticipant participant,
        string position,
        ICollection<CompetitionContractError> errors)
    {
        if (registration is null)
        {
            errors.Add(new("admission.bot-build.not-found",
                $"Build '{participant.BuildId}' was not found for bot identity '{participant.BotIdentityId}'."));
            return;
        }

        var build = registration.Build;
        if (build is null ||
            !string.Equals(build.BotIdentityId, participant.BotIdentityId, StringComparison.Ordinal) ||
            !string.Equals(build.BuildId, participant.BuildId, StringComparison.Ordinal))
        {
            errors.Add(new($"admission.bot-build.{position}.identity-mismatch",
                $"The catalog result does not match the requested bot identity and exact build ID for the {position} participant."));
            return;
        }

        if (registration.Eligibility != BotBuildEligibilityStatus.Eligible)
            errors.Add(new($"admission.bot-build.{position}.ineligible",
                $"Build '{participant.BuildId}' is not eligible for competition (status: {registration.Eligibility})."));
    }

    private static void ValidateParticipant(
        MatchAdmissionParticipant? participant,
        string position,
        ICollection<CompetitionContractError> errors)
    {
        if (participant is null)
        {
            errors.Add(new($"admission.participant.{position}.required",
                $"The {position} participant is required."));
            return;
        }

        if (string.IsNullOrWhiteSpace(participant.ParticipantId))
            errors.Add(new($"admission.participant.{position}.id",
                $"The {position} participant ID must be non-empty."));
        if (string.IsNullOrWhiteSpace(participant.BotIdentityId))
            errors.Add(new($"admission.bot-identity.{position}.id",
                $"The {position} bot identity ID must be non-empty."));
        if (string.IsNullOrWhiteSpace(participant.BuildId))
            errors.Add(new($"admission.bot-build.{position}.id",
                $"The {position} build ID must be non-empty."));
    }
}
