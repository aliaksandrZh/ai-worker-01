namespace PiAgent.Infrastructure.Persistence;

/// <summary>Maps an operation to a profile version, optionally per repository (02 §12a).</summary>
public class AgentProfileRoutingRule
{
    public Guid Id { get; set; }
    /// <summary>NULL = global default; V1 seeds the global rule set (repository_id = NULL).</summary>
    public Guid? RepositoryId { get; set; }
    /// <summary>`plan` | `implement` | `review` | `test_design` (02 §12a).</summary>
    public string TaskType { get; set; } = null!;
    /// <summary>References the version that was current at sync time; ready-gating happens at Task selection (owner ruling, decisions 81/36).</summary>
    public Guid AgentProfileVersionId { get; set; }
    public int Priority { get; set; }
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
