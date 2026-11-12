namespace ServerApp.Core;

/// <summary>
/// Host-independent definition of one competition. This is a ladder-domain contract,
/// not a ServerRoleManifest, FSM_COS RuntimeManifest, or Foundry WorkflowDefinition.
/// </summary>
public sealed class CompetitionDefinition
{
    public CompetitionDefinition(
        string competitionId,
        string gameId,
        string rulesetId,
        string rulesetVersion,
        IEnumerable<CompetitionMatchFormat> formats)
    {
        CompetitionId = competitionId ?? throw new ArgumentNullException(nameof(competitionId));
        GameId = gameId ?? throw new ArgumentNullException(nameof(gameId));
        RulesetId = rulesetId ?? throw new ArgumentNullException(nameof(rulesetId));
        RulesetVersion = rulesetVersion ?? throw new ArgumentNullException(nameof(rulesetVersion));
        ArgumentNullException.ThrowIfNull(formats);
        Formats = Array.AsReadOnly(formats.Select(format =>
            format ?? throw new ArgumentException("A competition format cannot be null.", nameof(formats))).ToArray());
    }

    public string CompetitionId { get; }
    public string GameId { get; }
    public string RulesetId { get; }
    public string RulesetVersion { get; }
    public IReadOnlyList<CompetitionMatchFormat> Formats { get; }
}

/// <summary>
/// Team topology for a match format. Each integer is the number of participant slots
/// on one team; independent FFA participants are represented as one-member teams.
/// </summary>
public sealed class CompetitionMatchFormat
{
    public CompetitionMatchFormat(string formatId, IEnumerable<int> teamSizes)
    {
        FormatId = formatId ?? throw new ArgumentNullException(nameof(formatId));
        ArgumentNullException.ThrowIfNull(teamSizes);
        TeamSizes = Array.AsReadOnly(teamSizes.ToArray());
    }

    public string FormatId { get; }
    public IReadOnlyList<int> TeamSizes { get; }
    public int ParticipantCount => TeamSizes.Sum();
}

/// <summary>An immutable bot build identity resolved before a match is admitted.</summary>
public sealed record BotBuildReference(
    string BotIdentityId,
    string BuildId,
    string ContentSha256);

/// <summary>A participant's immutable build and match-scoped team assignment.</summary>
public sealed record MatchParticipantAssignment(
    string ParticipantId,
    string TeamId,
    BotBuildReference BotBuild);

/// <summary>
/// Immutable admission snapshot for one match. It pins the competition/ruleset,
/// game adapter version, participant builds, and team topology before launch.
/// </summary>
public sealed class MatchAssignment
{
    public MatchAssignment(
        string matchId,
        string competitionId,
        string gameId,
        string rulesetId,
        string rulesetVersion,
        string formatId,
        string gameAdapterVersion,
        DateTimeOffset assignedAtUtc,
        IEnumerable<MatchParticipantAssignment> participants)
    {
        MatchId = matchId ?? throw new ArgumentNullException(nameof(matchId));
        CompetitionId = competitionId ?? throw new ArgumentNullException(nameof(competitionId));
        GameId = gameId ?? throw new ArgumentNullException(nameof(gameId));
        RulesetId = rulesetId ?? throw new ArgumentNullException(nameof(rulesetId));
        RulesetVersion = rulesetVersion ?? throw new ArgumentNullException(nameof(rulesetVersion));
        FormatId = formatId ?? throw new ArgumentNullException(nameof(formatId));
        GameAdapterVersion = gameAdapterVersion ?? throw new ArgumentNullException(nameof(gameAdapterVersion));
        AssignedAtUtc = assignedAtUtc;
        ArgumentNullException.ThrowIfNull(participants);
        Participants = Array.AsReadOnly(participants.Select(participant =>
            participant ?? throw new ArgumentException("A match participant cannot be null.", nameof(participants))).ToArray());
    }

    public string MatchId { get; }
    public string CompetitionId { get; }
    public string GameId { get; }
    public string RulesetId { get; }
    public string RulesetVersion { get; }
    public string FormatId { get; }
    public string GameAdapterVersion { get; }
    public DateTimeOffset AssignedAtUtc { get; }
    public IReadOnlyList<MatchParticipantAssignment> Participants { get; }
}

public sealed record CompetitionContractError(string Code, string Message);

/// <summary>Fail-closed validation for competition definitions and immutable match assignments.</summary>
public static class CompetitionContractValidator
{
    public static IReadOnlyList<CompetitionContractError> ValidateDefinition(CompetitionDefinition? definition)
    {
        var errors = new List<CompetitionContractError>();
        if (definition is null)
        {
            errors.Add(new("competition.required", "A competition definition is required."));
            return errors;
        }

        Require(definition.CompetitionId, "competition.id", "CompetitionId", errors);
        Require(definition.GameId, "competition.game", "GameId", errors);
        Require(definition.RulesetId, "competition.ruleset", "RulesetId", errors);
        Require(definition.RulesetVersion, "competition.ruleset.version", "RulesetVersion", errors);

        if (definition.Formats.Count == 0)
            errors.Add(new("competition.formats.required", "At least one match format must be declared."));

        var formatIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var format in definition.Formats)
        {
            if (string.IsNullOrWhiteSpace(format.FormatId))
                errors.Add(new("competition.format.id", "Every match format must have a non-empty FormatId."));
            else if (!formatIds.Add(format.FormatId))
                errors.Add(new("competition.format.duplicate", $"FormatId '{format.FormatId}' is declared more than once."));

            if (format.TeamSizes.Count < 2 || format.TeamSizes.Any(size => size <= 0) ||
                format.ParticipantCount > 64)
                errors.Add(new("competition.format.topology",
                    $"Format '{format.FormatId}' must declare 2 or more teams, positive team sizes, and at most 64 participants."));
        }

        return errors;
    }

    public static IReadOnlyList<CompetitionContractError> ValidateAssignment(
        CompetitionDefinition? definition,
        MatchAssignment? assignment)
    {
        var errors = new List<CompetitionContractError>();
        var definitionErrors = ValidateDefinition(definition);
        errors.AddRange(definitionErrors);
        if (definitionErrors.Count > 0 || assignment is null)
        {
            errors.Add(new("match.assignment.required", "A match assignment is required."));
            return errors;
        }
        if (definition is null)
            return errors;

        Require(assignment.MatchId, "match.id", "MatchId", errors);
        Require(assignment.GameAdapterVersion, "match.adapter.version", "GameAdapterVersion", errors);
        if (assignment.AssignedAtUtc.Offset != TimeSpan.Zero)
            errors.Add(new("match.assigned-at.utc", "AssignedAtUtc must use the UTC offset."));

        MatchValue(assignment.CompetitionId, definition.CompetitionId, "match.competition", "CompetitionId", errors);
        MatchValue(assignment.GameId, definition.GameId, "match.game", "GameId", errors);
        MatchValue(assignment.RulesetId, definition.RulesetId, "match.ruleset", "RulesetId", errors);
        MatchValue(assignment.RulesetVersion, definition.RulesetVersion, "match.ruleset.version", "RulesetVersion", errors);

        var format = definition.Formats.SingleOrDefault(item =>
            string.Equals(item.FormatId, assignment.FormatId, StringComparison.Ordinal));
        if (format is null)
        {
            errors.Add(new("match.format.unsupported", $"Format '{assignment.FormatId}' is not supported by this competition."));
            return errors;
        }

        if (assignment.Participants.Count != format.ParticipantCount)
            errors.Add(new("match.participants.count",
                $"Format '{format.FormatId}' requires {format.ParticipantCount} participants; received {assignment.Participants.Count}."));

        var participantIds = new HashSet<string>(StringComparer.Ordinal);
        var botIds = new HashSet<string>(StringComparer.Ordinal);
        var teamIds = new HashSet<string>(StringComparer.Ordinal);
        var actualTeamSizes = new List<int>();
        foreach (var participant in assignment.Participants)
        {
            if (string.IsNullOrWhiteSpace(participant.ParticipantId) ||
                !participantIds.Add(participant.ParticipantId))
                errors.Add(new("match.participant.id", "Participant IDs must be non-empty and unique."));
            if (string.IsNullOrWhiteSpace(participant.TeamId))
                errors.Add(new("match.team.id", "Every participant must belong to a non-empty match team."));
            else
                teamIds.Add(participant.TeamId);

            if (participant.BotBuild is null)
            {
                errors.Add(new("match.bot-build.required", "Every participant must pin a bot build."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(participant.BotBuild.BotIdentityId) ||
                !botIds.Add(participant.BotBuild.BotIdentityId))
                errors.Add(new("match.bot-identity.unique", "Bot identity IDs must be non-empty and unique within a match."));
            Require(participant.BotBuild.BuildId, "match.bot-build.id", "BuildId", errors);
            var hash = participant.BotBuild.ContentSha256;
            if (string.IsNullOrEmpty(hash) || hash.Length != 64 ||
                hash.Any(character => !Uri.IsHexDigit(character)))
                errors.Add(new("match.bot-build.hash", "Every bot build must pin a 64-character hexadecimal SHA-256 content hash."));
        }

        foreach (var teamId in teamIds)
            actualTeamSizes.Add(assignment.Participants.Count(participant =>
                string.Equals(participant.TeamId, teamId, StringComparison.Ordinal)));

        if (!actualTeamSizes.OrderBy(size => size).SequenceEqual(format.TeamSizes.OrderBy(size => size)))
            errors.Add(new("match.teams.topology",
                $"Participant team sizes do not match the declared topology for format '{format.FormatId}'."));

        return errors;
    }

    private static void Require(
        string value,
        string code,
        string name,
        ICollection<CompetitionContractError> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add(new(code, $"{name} must be non-empty."));
    }

    private static void MatchValue(
        string actual,
        string expected,
        string code,
        string name,
        ICollection<CompetitionContractError> errors)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            errors.Add(new(code, $"{name} does not match the competition definition."));
    }
}
