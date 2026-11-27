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
        workflowPath: ".github/workflows/execution-fsm-api-probe.yml",
        requiredJobKey: "fsm-api-proof",
        requiredJobName: "Build, execute, and capture state evidence",
        resolutionStepName: "Resolve candidate",
        verificationStepName: "Verify candidate checkout",
        provenanceArtifactPrefix: "ciai-workflow-provenance",
        testArtifactPrefix: "ciai-test-results",
        trxEntryName: "results.trx",
        provenanceEntryName: "workflow-provenance.json",
        testProviderLabel: "dotnet-vstest",
        requiredCheckDisplayName: "Execution FSM_API and Fox/Sheepdog Proof");
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
}
