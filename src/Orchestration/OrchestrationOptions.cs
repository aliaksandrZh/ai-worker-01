namespace PiAgent.Orchestration;

/// <summary>
/// Orchestrator policies (docs/orchestrator.md §4, mirrored in flows/README.md): applied
/// while processing events, never drawn as flow nodes.
/// </summary>
public sealed class OrchestrationOptions
{
    public const string SectionName = "Orchestrator";

    /// <summary>Minimum GitHub permission a command author must have (payload-resolved).</summary>
    public string MinCommandPermission { get; set; } = "write";

    /// <summary>Does a new implementation command require an approved plan?</summary>
    public bool PlanApprovalRequired { get; set; } = true;

    /// <summary>Do passing test results require manual user acceptance?</summary>
    public bool ManualAcceptanceRequired { get; set; }

    /// <summary>Review → fixing rounds before the Workflow parks.</summary>
    public int MaxReviewRounds { get; set; } = 3;

    /// <summary>Auto-retries of a failed Run before the Task parks.</summary>
    public int MaxRunRetries { get; set; } = 2;
}
