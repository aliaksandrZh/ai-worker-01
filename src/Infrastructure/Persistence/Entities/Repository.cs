namespace PiAgent.Infrastructure.Persistence;

/// <summary>Configured GitHub repository (02 §2).</summary>
public class Repository
{
    public Guid Id { get; set; }
    public long GithubRepositoryId { get; set; }
    public string Owner { get; set; } = null!;
    public string Name { get; set; } = null!;
    /// <summary>Normalized lowercase `owner/name`; unique (02 §2).</summary>
    public string FullName { get; set; } = null!;
    public string DefaultBranch { get; set; } = null!;
    /// <summary>False = paused: task-producing commands rejected, polling continues read-only (decision 75).</summary>
    public bool IsEnabled { get; set; }
    /// <summary>Authorization configuration from repositories.yaml (02 §21): allowlist, permitted commands/types, policy.</summary>
    public string Configuration { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
