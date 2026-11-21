namespace PiAgent.Infrastructure.Persistence;

/// <summary>
/// Immutable record of the input frozen for one Run (02 §14a, decision 63) — built by the Orchestrator's
/// Context Assembler before dispatch. Snapshots are never modified; serving the input to the Bridge
/// reads this row, and restarts re-read it (no re-assembly). `content` holds the serialized input
/// (04 §3 shape) so the Runtime API can serve it; ticket 11 replaces the assembler, never the immutability.
/// </summary>
public class RunContextSnapshot
{
    public Guid Id { get; set; }
    /// <summary>One snapshot per Run (02 §23).</summary>
    public Guid RunId { get; set; }
    public Guid TaskId { get; set; }
    /// <summary>sha256 hex over the serialized content.</summary>
    public string ContentHash { get; set; } = null!;
    /// <summary>JSON: what went in, with the source references (decision 63).</summary>
    public string IncludedSources { get; set; } = null!;
    /// <summary>JSON: what was pruned and why (V1 stub: `[]`; ticket 11 fills the pruning machinery).</summary>
    public string ExcludedSources { get; set; } = null!;
    /// <summary>The frozen serialized input (jsonb) served to the Bridge via `GET …/input`.</summary>
    public string Content { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}
