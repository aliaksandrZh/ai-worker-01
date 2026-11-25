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

    /// <summary>
    /// Default (PR-base) branch per repository, key "owner/name" — read from the config
    /// plane's repositories.yaml (optional default_branch per entry) at wiring time.
    /// </summary>
    public IReadOnlyDictionary<string, string> DefaultBranches { get; set; } =
        new Dictionary<string, string>();

    /// <summary>The default branch for a repository with no config entry.</summary>
    public string DefaultBranchFallback { get; set; } = "main";

    /// <summary>
    /// Profile secret NAMES per agent profile (the config plane's frozen copy of
    /// agents/&lt;name&gt;/profile.yaml `secrets:`): frozen into run_scheduled; the Run Worker
    /// binds the host-managed files read-only at /run/secrets/&lt;name&gt;. Only names travel
    /// through configuration — values never do.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ProfileSecrets { get; set; } =
        new Dictionary<string, IReadOnlyList<string>>();
}
