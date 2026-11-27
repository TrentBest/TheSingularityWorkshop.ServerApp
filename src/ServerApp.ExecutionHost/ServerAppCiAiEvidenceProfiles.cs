using TheSingularityWorkshop.CiAi;

namespace ServerApp.ExecutionHost;

/// <summary>
/// Trusted observer profiles for workflows that ServerApp is explicitly configured to accept.
/// The profile must match the workflow job/step names and artifact naming contract exactly.
/// </summary>
public static class ServerAppCiAiEvidenceProfiles
{
    public static CiWorkflowObserverProfile ExecutionDefault { get; } = new(
        workflowPath: ".github/workflows/execution-fsm-api-probe.yml",
        requiredJobKey: "build-and-execute",
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
