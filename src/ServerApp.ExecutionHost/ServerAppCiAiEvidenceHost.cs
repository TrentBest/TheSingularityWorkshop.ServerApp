using TheSingularityWorkshop.CiAi;
using TheSingularityWorkshop.Execution;

namespace ServerApp.ExecutionHost;

/// <summary>
/// One authenticated producer notice and independently accepted workflow packet, paired
/// with the separately verified test-results archive used only for diagnostics.
/// </summary>
public sealed record ServerAppAcceptedCiAiEvidence(
    CiAiRequestTaskMappingSnapshot Request,
    ExecutionAcceptedCiAiWorkflowEvidence WorkflowEvidence,
    CiTestResultArtifactArchive TestResultsArchive,
    CiTestResultProviderArtifactBinding TestResultsSourceBinding,
    CiTestResultReport TestResultReport,
    DateTimeOffset ObservedAt);

/// <summary>
/// ServerApp composition root for the authenticated CiAi producer → Execution consumer path.
/// Notice authentication, mapping lookup, exact artifact retrieval, independent provider
/// observation, protected workflow trust, and durable V1 packet acceptance are separate gates.
/// All network clients, authenticators, and credential providers are host supplied.
/// </summary>
public sealed class ServerAppCiAiEvidenceHost
{
    private readonly JsonlCiAiRequestTaskMappingJournal _mappingJournal;
    private readonly JsonlCiAiArtifactNoticeReceiptLedger _noticeReceipts;
    private readonly ICiAiArtifactTransport _artifactTransport;
    private readonly string _receiverIdentity;
    private readonly string _providerRepository;
    private readonly IExecutionCiAiProviderRunObserver _providerObserver;
    private readonly IGitHubActionsArtifactArchiveDownloader _artifactDownloader;
    private readonly IGitHubActionsWorkflowDefinitionReader _workflowReader;
    private readonly JsonlCiAiWorkflowEvidencePacketStore _packetStore;
    private readonly CiWorkflowObserverProfile _observerProfile;

    public ServerAppCiAiEvidenceHost(
        JsonlCiAiRequestTaskMappingJournal mappingJournal,
        JsonlCiAiArtifactNoticeReceiptLedger noticeReceipts,
        ICiAiArtifactTransport artifactTransport,
        string receiverIdentity,
        string providerRepository,
        IExecutionCiAiProviderRunObserver providerObserver,
        IGitHubActionsArtifactArchiveDownloader artifactDownloader,
        IGitHubActionsWorkflowDefinitionReader workflowReader,
        JsonlCiAiWorkflowEvidencePacketStore packetStore,
        CiWorkflowObserverProfile observerProfile)
    {
        _mappingJournal = mappingJournal ?? throw new ArgumentNullException(nameof(mappingJournal));
        _noticeReceipts = noticeReceipts ?? throw new ArgumentNullException(nameof(noticeReceipts));
        _artifactTransport = artifactTransport ?? throw new ArgumentNullException(nameof(artifactTransport));
        ArgumentException.ThrowIfNullOrWhiteSpace(receiverIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerRepository);
        _receiverIdentity = receiverIdentity;
        _providerRepository = providerRepository;
        _providerObserver = providerObserver ?? throw new ArgumentNullException(nameof(providerObserver));
        _artifactDownloader = artifactDownloader ?? throw new ArgumentNullException(nameof(artifactDownloader));
        _workflowReader = workflowReader ?? throw new ArgumentNullException(nameof(workflowReader));
        _packetStore = packetStore ?? throw new ArgumentNullException(nameof(packetStore));
        _observerProfile = observerProfile ?? throw new ArgumentNullException(nameof(observerProfile));
    }

    /// <summary>
    /// Accepts the exact producer notice body through request-scoped authentication, validates
    /// it against the persisted dispatch mapping, retrieves and hashes the immutable packet,
    /// then independently verifies the provider run and protected workflow before accepting V1.
    /// </summary>
    public async Task<ServerAppAcceptedCiAiEvidence> AcceptPublishedEvidenceAsync(
        CiAiRequestTaskMappingSnapshot expectedRequest,
        ReadOnlyMemory<byte> exactNoticeBody,
        ICiAiArtifactNoticeAuthenticator requestAuthenticator,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedRequest);
        ArgumentNullException.ThrowIfNull(requestAuthenticator);
        if (receivedAt == default)
            throw new ArgumentException("Notice receipt time is required.", nameof(receivedAt));
        if (expectedRequest.Cancelled)
            throw new InvalidOperationException("Cancelled dispatch intent cannot accept workflow evidence.");

        var expectedMapping = expectedRequest.Mapping.Validate();
        var expectedRunBinding = expectedRequest.RunBinding
            ?? throw new InvalidOperationException("Published workflow evidence requires a persisted exact-run binding.");
        expectedRunBinding.ValidateAgainst(expectedMapping);

        // The authenticator is request-scoped. It must verify issuer, audience, signature,
        // subject, scope, expiry, and sender-repository authorization before returning identity.
        var noticePolicy = new CiAiArtifactNoticeValidationPolicy(
            _receiverIdentity,
            _providerRepository,
            CiWorkflowEvidenceExecutionNoticeEnqueuer.CapabilityId,
            CiWorkflowEvidenceExecutionNoticeEnqueuer.CapabilityVersion,
            expectedMapping.TrustedWorkflowSha);
        var ingress = new CiAiArtifactNoticeIngress(
            requestAuthenticator, _mappingJournal, noticePolicy, _noticeReceipts);
        var ingressResult = await ingress.AcceptAsync(
            exactNoticeBody, receivedAt, cancellationToken).ConfigureAwait(false);

        var validatedRequest = ingressResult.ValidatedNotice.Request;
        EnsureSameDispatch(expectedRequest, validatedRequest);
        var mapping = validatedRequest.Mapping.Validate();
        var runBinding = validatedRequest.RunBinding
            ?? throw new InvalidOperationException("Authenticated artifact notice has no exact-run binding.");

        var retrieval = new CiAiArtifactRetrievalCoordinator(_artifactTransport);
        var retrievedArtifact = await retrieval.RetrieveAndVerifyAsync(
            ingressResult.ValidatedNotice, receivedAt, cancellationToken).ConfigureAwait(false);

        // Re-observe GitHub independently. The notice is metadata, not evidence that the
        // provider run, candidate checkout, or protected workflow actually produced the packet.
        var providerRun = await _providerObserver.ObserveAsync(
            validatedRequest, _observerProfile, receivedAt, cancellationToken).ConfigureAwait(false);
        var expectedProviderEventSha = ServerAppCiAiEvidenceIdentity.ResolveProviderEventSha(mapping, runBinding);

        var provenanceArchive = await _artifactDownloader.DownloadAsync(
            providerRun.ProvenanceArtifact,
            mapping.Repository,
            runBinding.RunId,
            expectedProviderEventSha,
            receivedAt,
            cancellationToken).ConfigureAwait(false);
        var testResultsArchive = await _artifactDownloader.DownloadAsync(
            providerRun.TestResultsArtifact,
            mapping.Repository,
            runBinding.RunId,
            expectedProviderEventSha,
            receivedAt,
            cancellationToken).ConfigureAwait(false);

        // The workflow-definition SHA is not the provider event/head SHA. Read the exact
        // protected workflow revision from the dispatch mapping; do not resolve a mutable ref.
        var approvedWorkflowBytes = await _workflowReader.ReadAsync(
            mapping.Repository,
            mapping.WorkflowPath,
            mapping.TrustedWorkflowSha,
            cancellationToken).ConfigureAwait(false);

        var accepted = ExecutionCiAiWorkflowEvidenceAcceptanceService.Accept(
            ingressResult,
            retrievedArtifact,
            providerRun.Run,
            provenanceArchive,
            _observerProfile,
            CiWorkflowEvidenceExecutionNoticeEnqueuer.CapabilityId,
            CiWorkflowEvidenceExecutionNoticeEnqueuer.CapabilityVersion,
            mapping.TrustedWorkflowSha,
            approvedWorkflowBytes,
            approvedWorkflowBytes,
            _packetStore,
            receivedAt);

        var expectedTestArtifactName = _observerProfile.TestArtifactName(runBinding.RunId, runBinding.RunAttempt);
        if (!string.Equals(mapping.SourceArtifactName, expectedTestArtifactName, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Persisted request mapping does not authorize the exact profile-bound test-results artifact name.");

        var requiredJobPolicy = _observerProfile.CreateRequiredJobPolicy(runBinding.RunId, runBinding.RunAttempt);
        var requiredJobs = accepted.Packet.ObservedRun.Jobs.Where(job =>
            job.RunId == runBinding.RunId &&
            job.RunAttempt == runBinding.RunAttempt &&
            string.Equals(job.Name, mapping.JobName, StringComparison.Ordinal)).ToArray();
        if (requiredJobs.Length != 1)
            throw new InvalidDataException("The trusted run does not contain exactly one registered required job for test-result binding.");

        var testResultsBinding = CiTestResultProviderArtifactBinding.FromObservedArtifact(
            providerRun.TestResultsArtifact,
            accepted.Packet,
            requiredJobPolicy,
            requiredJobs[0].JobId,
            mapping.JobKey!);
        var verifiedTestResultsArchive = CiTestResultArtifactArchive.Read(
            testResultsArchive,
            testResultsBinding,
            _observerProfile.TrxEntryName,
            _observerProfile.ProvenanceEntryName);
        var testResultSource = CiTestResultArtifactSource.FromVerifiedProviderObservation(
            accepted.Packet,
            requiredJobPolicy,
            testResultsBinding,
            verifiedTestResultsArchive,
            _observerProfile.TestProviderLabel);
        var testResultReport = CiTestResultReport.ParseAndValidate(
            verifiedTestResultsArchive.TrxBytes,
            accepted.Packet,
            testResultSource,
            requiredJobPolicy,
            receivedAt);

        return new ServerAppAcceptedCiAiEvidence(
            validatedRequest,
            accepted,
            verifiedTestResultsArchive,
            testResultsBinding,
            testResultReport,
            receivedAt);
    }

    private static void EnsureSameDispatch(
        CiAiRequestTaskMappingSnapshot expected,
        CiAiRequestTaskMappingSnapshot actual)
    {
        var left = expected.Mapping.Validate();
        var right = actual.Mapping.Validate();
        var leftRun = expected.RunBinding
            ?? throw new InvalidOperationException("Expected dispatch has no exact run binding.");
        var rightRun = actual.RunBinding
            ?? throw new InvalidOperationException("Authenticated dispatch has no exact run binding.");
        if (expected.Cancelled || actual.Cancelled ||
            !string.Equals(left.RequestId, right.RequestId, StringComparison.Ordinal) ||
            !string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal) ||
            !string.Equals(left.IdempotencyKey, right.IdempotencyKey, StringComparison.Ordinal) ||
            !string.Equals(left.TaskId, right.TaskId, StringComparison.Ordinal) ||
            !string.Equals(left.CorrelationId, right.CorrelationId, StringComparison.Ordinal) ||
            !string.Equals(left.Repository, right.Repository, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(left.CandidateRef, right.CandidateRef, StringComparison.Ordinal) ||
            !string.Equals(left.CandidateSha, right.CandidateSha, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(left.WorkflowPath, right.WorkflowPath, StringComparison.Ordinal) ||
            !string.Equals(left.TrustedWorkflowSha, right.TrustedWorkflowSha, StringComparison.OrdinalIgnoreCase) ||
            leftRun.RunId != rightRun.RunId || leftRun.RunAttempt != rightRun.RunAttempt ||
            !string.Equals(leftRun.EventSha, rightRun.EventSha, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Authenticated artifact notice resolved a different persisted dispatch identity.");
    }
}
