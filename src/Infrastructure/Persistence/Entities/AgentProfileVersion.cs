namespace PiAgent.Infrastructure.Persistence;

/// <summary>AgentProfileVersion state values.</summary>
public static class ProfileVersionStates
{
    public const string Draft = "draft";
    public const string Building = "building";
    public const string Ready = "ready";
    public const string BuildFailed = "build_failed";
    public const string Disabled = "disabled";
}

/// <summary>Immutable pinned snapshot of one profile configuration; created by the Config Loader in `draft`.</summary>
public class AgentProfileVersion
{
    public Guid Id { get; set; }
    public Guid AgentProfileId { get; set; }
    /// <summary>Sequential per profile; next = latest.Version + 1 when config_hash differs.</summary>
    public int Version { get; set; }
    /// <summary>Optional traceability metadata: the system-repo commit when PI_AGENT_SOURCE_COMMIT is set. Never a build input.</summary>
    public string? SourceCommitSha { get; set; }
    /// <summary>Git directory of the profile within the system repository, e.g. `config/agents/planner`.</summary>
    public string SourceDirectory { get; set; } = null!;
    /// <summary>Immutable jsonb snapshot: normalized profile.yaml + per-file sha256 + file contents; rebuildable without Git.</summary>
    public string ConfigSnapshot { get; set; } = null!;
    /// <summary>sha256 hex over the canonical snapshot JSON; identity of the version for dedup.</summary>
    public string ConfigHash { get; set; } = null!;
    /// <summary>`pi`.</summary>
    public string RuntimeType { get; set; } = null!;
    public string? SystemPrompt { get; set; }
    public string? SkillsConfiguration { get; set; }
    public string? McpConfiguration { get; set; }
    public string? ToolsConfiguration { get; set; }
    /// <summary>Secret references only — values live in host-managed files.</summary>
    public string? EnvironmentConfiguration { get; set; }
    public string? ResourceConfiguration { get; set; }
    public string? Capabilities { get; set; }
    public string? RetryPolicy { get; set; }
    /// <summary>Tool/MCP allow-list; unattended mode denies everything else.</summary>
    public string? PermissionPolicy { get; set; }
    /// <summary>Runtime Bridge version + heartbeat/event cadence.</summary>
    public string? BridgeConfiguration { get; set; }
    /// <summary>Profile execution-limit defaults; NULL = system default governs. May lower, never exceed system values.</summary>
    public TimeSpan? StartupTimeout { get; set; }
    public TimeSpan? IdleTimeout { get; set; }
    public TimeSpan? MaxRunDuration { get; set; }
    /// <summary>Mandatory per profile; only presence/positivity is validated.</summary>
    public long MaxContextTokens { get; set; }
    /// <summary>Set `draft` here; `building/ready/build_failed` transitions are Orchestrator-owned.</summary>
    public string State { get; set; } = null!;
    /// <summary>NULL until the image build records it (tag `agent-&lt;profile&gt;:v&lt;n&gt;`).</summary>
    public string? ContainerImage { get; set; }
    public string? ImageDigest { get; set; }
    public DateTime CreatedAt { get; set; }
}
