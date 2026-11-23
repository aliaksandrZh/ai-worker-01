namespace PiAgent.Infrastructure.Persistence;

/// <summary>Configured GitHub repository.</summary>
public class Repository
{
    public Guid Id { get; set; }
    public long GithubRepositoryId { get; set; }
    public string Owner { get; set; } = null!;
    public string Name { get; set; } = null!;
    /// <summary>Normalized lowercase `owner/name`; unique.</summary>
    public string FullName { get; set; } = null!;
    public string DefaultBranch { get; set; } = null!;
    /// <summary>False = paused: task-producing commands rejected, polling continues read-only.</summary>
    public bool IsEnabled { get; set; }
    /// <summary>Authorization configuration from repositories.yaml: allowlist, permitted commands/types, policy.</summary>
    public string Configuration { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
