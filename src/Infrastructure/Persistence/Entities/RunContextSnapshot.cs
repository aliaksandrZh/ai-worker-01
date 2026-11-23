namespace PiAgent.Infrastructure.Persistence;

/// <summary>
/// Immutable record of the input frozen for one Run — built by the Orchestrator's
/// Context Assembler before dispatch. Snapshots are never modified; serving the input to the Bridge
/// reads this row, and restarts re-read it (no re-assembly). `content` holds the serialized input
/// so the Runtime API can serve it.
/// </summary>
public class RunContextSnapshot
{
    public Guid Id { get; set; }
    /// <summary>One snapshot per Run.</summary>
    public Guid RunId { get; set; }
    public Guid TaskId { get; set; }
    /// <summary>sha256 hex over the serialized content.</summary>
    public string ContentHash { get; set; } = null!;
    /// <summary>JSON: what went in, with the source references.</summary>
    public string IncludedSources { get; set; } = null!;
    /// <summary>JSON: what was pruned and why (stub: `[]`).</summary>
    public string ExcludedSources { get; set; } = null!;
    /// <summary>The frozen serialized input (jsonb) served to the Bridge via `GET …/input`.</summary>
    public string Content { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}
