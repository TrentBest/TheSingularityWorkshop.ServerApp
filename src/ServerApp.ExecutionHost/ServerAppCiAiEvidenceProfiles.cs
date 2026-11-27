using TheSingularityWorkshop.CiAi;
using TheSingularityWorkshop.Execution;

namespace ServerApp.ExecutionHost;

/// <summary>
/// Trusted observer profiles for workflows that ServerApp is explicitly configured to accept.
/// The profile must match the workflow job/step names and artifact naming contract exactly.
/// </summary>
public static class ServerAppCiAiEvidenceProfiles
{
    public static CiWorkflowObserverProfile ExecutionDefault { get; } = new(
        id: "execution-fsm-api-probe-v1",
        workflowPath: ".github/workflows/execution-fsm-api-probe.yml",
        provenanceArtifactNameTemplate: "ciai-workflow-provenance-{attempt}",
        testArtifactNameTemplate: "ciai-test-results-{attempt}",
        provenanceEntryName: "workflow-provenance.json",
        trxEntryName: "results.trx",
        testProviderLabel: "dotnet-vstest",
        requiredJobKey: "fsm-api-proof",
        requiredJobName: "Build, execute, and capture state evidence",
        resolutionStepName: "Resolve candidate",
        verificationStepName: "Verify candidate checkout");
}


/// <summary>
/// Resolves the provider's event/head SHA from the persisted exact-run binding.
/// This is deliberately distinct from the trusted workflow-definition SHA.
/// </summary>
public static class ServerAppCiAiEvidenceIdentity
{
    public static string ResolveProviderEventSha(
        CiAiRequestTaskMapping mapping,
        CiAiDispatchRunBinding runBinding)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(runBinding);
        mapping.Validate();
        runBinding.ValidateAgainst(mapping);
        return runBinding.EventSha ?? mapping.TrustedEventSha ?? mapping.TrustedWorkflowSha;
    }

    /// <summary>
    /// Projects the exact pre-dispatch identity into CiAi's transport-neutral delivery contract.
    /// Never generate a new request, operation, or idempotency identity at evidence-publication time.
    /// </summary>
    public static CiAiArtifactDeliveryIdentity CreateDeliveryIdentity(CiAiRequestTaskMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        mapping.Validate();
        return new CiAiArtifactDeliveryIdentity(
            mapping.RequestId, mapping.OperationId, mapping.IdempotencyKey).Validate();
    }
}
