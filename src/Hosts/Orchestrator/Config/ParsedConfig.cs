namespace PiAgent.Orchestrator.Config;

/// <summary>Parsed configuration-plane desired state: YAML is the source of desired configuration.</summary>
public sealed class ParsedConfig
{
    public required SystemConfig System { get; init; }
    public required List<RepositoryEntry> Repositories { get; init; }
    public required List<RoutingRuleEntry> RoutingRules { get; init; }
    public required List<ProfileConfig> Profiles { get; init; }
    /// <summary>Board triggers from projects.yaml (`auto_plan` / `auto_code`); empty when the file
    /// is absent or no board declares one. Consumed by the project-event cycle (system-owned Tasks
    /// of the trigger's task type).</summary>
    public required List<ProjectBoardTrigger> ProjectBoardTriggers { get; init; } = [];
    /// <summary>Board status configs from projects.yaml (`ai_statuses`); empty when absent. Consumed
    /// by the dispatch/terminal paths (the AI Processing → In Progress and the implement-success →
    /// In Review board moves).</summary>
    public required List<ProjectStatusConfig> ProjectStatusConfigs { get; init; } = [];
}

/// <summary>
/// One Projects v2 board trigger (projects.yaml `auto_plan` / `auto_code`): when the Poller reports
/// an item's `status_field` moving `from → to` on the `owner/number` board, the project-event cycle
/// admits a system-owned Task of `task_type` on the item's work item — the board move IS the human
/// signal, no comment needed. Values compare ordinal (GitHub column names are exact-case).
/// </summary>
public sealed record ProjectBoardTrigger(
    string Owner, int Number, string StatusField, string From, string To, string TaskType)
{
    public string Board => $"{Owner}/{Number}";
}

/// <summary>
/// One Projects v2 board status config (projects.yaml `ai_statuses`): the column names the
/// Orchestrator drives on the `owner/number` board — `processing` when a run is handed off
/// (AI Processing), `review` when an implement Task succeeds (the PR is up). Null entries mean
/// "do not move the board for this state". `code_statuses` are the columns the MANUAL /code is
/// allowed in (STATUS + LABEL holds for the manual command too); an item not on a board (or on a
/// board without this key) cannot be verified — fail open. Values compare ordinal (exact-case).
/// </summary>
public sealed record ProjectStatusConfig(string Owner, int Number, string StatusField, string? Processing,
    string? Review, IReadOnlyList<string>? CodeStatuses = null)
{
    public string Board => $"{Owner}/{Number}";
}

public sealed class SystemConfig
{
    public required string BotLogin { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan PollOverlap { get; init; } = TimeSpan.FromMinutes(5);
    public long PerPage { get; init; } = 100;
    public int MaxConcurrentRuns { get; init; } = 10;
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(60);
    public TimeSpan MaxRunDuration { get; init; } = TimeSpan.FromHours(4);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);
    public long MaxLogSize { get; init; } = 50 * 1024 * 1024;
    public long MaxArtifactSize { get; init; } = 256 * 1024;
    public long MaxTaskArtifactsSize { get; init; } = 512 * 1024;
    public int MaxAutomaticRetries { get; init; } = 1;
    public TimeSpan OutboxRetryWindow { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan OrphanGracePeriod { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan OrphanQuarantinePeriod { get; init; } = TimeSpan.FromHours(24);
}

public sealed class RepositoryEntry
{
    public required string Owner { get; init; }
    public required string Name { get; init; }
    public bool Enabled { get; init; } = true;
    /// <summary>NULL = allowlist enabled and seeded with the owner on sync; empty list = filtering disabled.</summary>
    public List<string>? AllowedLogins { get; init; }
    public List<string>? PermittedCommands { get; init; }
    public List<string>? PermittedWorkItemTypes { get; init; }
    public string? MinimumPermission { get; init; }
}

public sealed class RoutingRuleEntry
{
    public required string TaskType { get; init; }
    public required string Profile { get; init; }
    /// <summary>`owner/name` when repo-scoped; NULL for the global default rule set.</summary>
    public string? Repository { get; init; }
    public int Priority { get; init; } = 1;
    public bool Enabled { get; init; } = true;
}

public sealed class ProfileConfig
{
    /// <summary>Equals the `config/agents/&lt;name&gt;/` directory name (single source; no `name` YAML key).</summary>
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public string RuntimeType { get; init; } = "pi";
    public string? SystemPrompt { get; init; }
    /// <summary>Mandatory per profile; only presence/positivity is validated.</summary>
    public long MaxContextTokens { get; init; }
    public TimeSpan? StartupTimeout { get; init; }
    public TimeSpan? IdleTimeout { get; init; }
    public TimeSpan? MaxRunDuration { get; init; }
    public decimal? Cpu { get; init; }
    public string? Memory { get; init; }
    public int? MaxAutomaticRetries { get; init; }
    public TimeSpan? InitialBackoff { get; init; }
    public string? BridgeVersion { get; init; }
    public TimeSpan? BridgeHeartbeatInterval { get; init; }
    public TimeSpan? BridgeEventCadence { get; init; }
    /// <summary>Stub-only scenario lever: `succeed` (default) | `fail` | `hang` | `never_start` | `permission_denied` | `mcp_failed`.</summary>
    public string? BridgeBehavior { get; init; }
    public List<string>? Skills { get; init; }
    /// <summary>Secret reference names only — values live in host-managed files.</summary>
    public List<string>? Secrets { get; init; }
    public List<string>? Capabilities { get; init; }
    /// <summary>Opaque passthrough structures, pinned verbatim into the snapshot for lineage.</summary>
    public Dictionary<string, object?>? Mcp { get; init; }
    public Dictionary<string, object?>? Tools { get; init; }
    public Dictionary<string, object?>? Permissions { get; init; }
}
