namespace PiAgent.Infrastructure.Persistence;

/// <summary>Maps an operation to a profile version, optionally per repository.</summary>
public class AgentProfileRoutingRule
{
    public Guid Id { get; set; }
    /// <summary>NULL = global default; the global rule set is seeded (repository_id = NULL).</summary>
    public Guid? RepositoryId { get; set; }
    /// <summary>`plan` | `implement` | `review` | `test_design`.</summary>
    public string TaskType { get; set; } = null!;
    /// <summary>References the version that was current at sync time; ready-gating happens at Task selection.</summary>
    public Guid AgentProfileVersionId { get; set; }
    public int Priority { get; set; }
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
