using TheSingularityWorkshop.CiAi;
using TheSingularityWorkshop.Execution;

namespace ServerApp.ExecutionHost;

/// <summary>
/// One independently observed CI result. Workflow evidence is durably staged by Execution;
/// the raw test-results archive remains available for CiAi's report builder and verdict consumer.
/// </summary>
public sealed record ServerAppAcceptedCiAiEvidence(
    ExecutionAcceptedCiAiWorkflowEvidence WorkflowEvidence,
    ReadOnlyMemory<byte> TestResultsArchiveBytes,
    CiProviderArtifactObservation TestResultsArtifact,
    DateTimeOffset ObservedAt);

/// <summary>
/// ServerApp composition root for the read-only GitHub → CiAi evidence path. All network clients
/// and credential providers are injected; the caller must supply pinned HTTPS clients and an
/// explicit storage-origin allowlist when constructing the archive downloader.
/// </summary>
public sealed class ServerAppCiAiEvidenceHost
{
    private readonly IExecutionCiAiProviderRunObserver _providerObserver;
    private readonly IGitHubActionsArtifactArchiveDownloader _artifactDownloader;
    private readonly IGitHubActionsWorkflowDefinitionReader _workflowReader;
    private readonly JsonlCiAiWorkflowEvidencePacketStore _packetStore;
    private readonly CiWorkflowObserverProfile _observerProfile;

    public ServerAppCiAiEvidenceHost(
        IExecutionCiAiProviderRunObserver providerObserver,
        IGitHubActionsArtifactArchiveDownloader artifactDownloader,
        IGitHubActionsWorkflowDefinitionReader workflowReader,
        JsonlCiAiWorkflowEvidencePacketStore packetStore,
        CiWorkflowObserverProfile observerProfile)
    {
        _providerObserver = providerObserver ?? throw new ArgumentNullException(nameof(providerObserver));
        _artifactDownloader = artifactDownloader ?? throw new ArgumentNullException(nameof(artifactDownloader));
        _workflowReader = workflowReader ?? throw new ArgumentNullException(nameof(workflowReader));
        _packetStore = packetStore ?? throw new ArgumentNullException(nameof(packetStore));
        _observerProfile = observerProfile ?? throw new ArgumentNullException(nameof(observerProfile));
    }

    /// <summary>
    /// Independently observes the exact persisted run, downloads and verifies both provider
    /// artifacts, reads the pinned workflow definition, and asks CiAi's canonical V1 contract
    /// to construct, validate, and durably stage the evidence packet.
    /// </summary>
    public async Task<ServerAppAcceptedCiAiEvidence> ObserveAndAcceptAsync(
        CiAiRequestTaskMappingSnapshot request,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (observedAt == default)
            throw new ArgumentException("Provider observation time is required.", nameof(observedAt));
        if (request.Cancelled)
            throw new InvalidOperationException("Cancelled dispatch intent cannot trigger provider observation.");

        var mapping = request.Mapping.Validate();
        var runBinding = request.RunBinding
            ?? throw new InvalidOperationException("CiAi provider observation requires a persisted exact-run binding.");
        runBinding.ValidateAgainst(mapping);

        var providerRun = await _providerObserver.ObserveAsync(
            request, _observerProfile, observedAt, cancellationToken).ConfigureAwait(false);

        var provenanceArchive = await _artifactDownloader.DownloadAsync(
            providerRun.ProvenanceArtifact,
            mapping.Repository,
            runBinding.RunId,
            mapping.TrustedWorkflowSha,
            observedAt,
            cancellationToken).ConfigureAwait(false);

        var testResultsArchive = await _artifactDownloader.DownloadAsync(
            providerRun.TestResultsArtifact,
            mapping.Repository,
            runBinding.RunId,
            mapping.TrustedWorkflowSha,
            observedAt,
            cancellationToken).ConfigureAwait(false);

        // The persisted mapping pins the protected workflow revision used when this run was
        // authorized. Read that exact commit; do not resolve a mutable branch name at acceptance.
        var approvedWorkflowBytes = await _workflowReader.ReadAsync(
            mapping.Repository,
            mapping.WorkflowPath,
            mapping.TrustedWorkflowSha,
            cancellationToken).ConfigureAwait(false);

        var accepted = ExecutionCiAiWorkflowEvidenceAcceptanceService.ObserveAndAccept(
            request,
            providerRun,
            provenanceArchive,
            _observerProfile,
            approvedWorkflowBytes,
            approvedWorkflowBytes,
            _packetStore,
            observedAt);

        return new ServerAppAcceptedCiAiEvidence(
            accepted, testResultsArchive, providerRun.TestResultsArtifact, observedAt);
    }
}
