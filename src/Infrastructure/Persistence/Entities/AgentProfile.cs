namespace PiAgent.Infrastructure.Persistence;

/// <summary>Versioned agent profile container (02 §12).</summary>
public class AgentProfile
{
    public Guid Id { get; set; }
    /// <summary>Profile name; equals its `config/agents/&lt;name&gt;/` directory name (single source).</summary>
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
