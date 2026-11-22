namespace PiAgent.Infrastructure.Persistence;

/// <summary>Orphan-candidate lifecycle (decision 77, ticket 13): candidate → quarantined → deleted.</summary>
public static class OrphanCandidateStatuses
{
    public const string Candidate = "candidate";
    public const string Quarantined = "quarantined";
    public const string Deleted = "deleted";
}

/// <summary>
/// One orphaned workflow storage tree under the managed root (decision 77): a directory whose
/// Workflow no longer exists in PostgreSQL (or whose storage is already marked deleted), observed by
/// the Host Monitor and resolved against the DB by the Runtime API. Candidates persist ≥ the grace
/// period, are quarantined (physically renamed) for the quarantine period, and only then are
/// physically deleted — and never while PostgreSQL is unavailable (this row can only be written when
/// it is) or while observed containers still bind the tree.
/// </summary>
public class OrphanCandidate
{
    public Guid Id { get; set; }
    /// <summary>Managed-root-relative directory name — the workflow GUID (or quarantined rename).</summary>
    public string DirectoryName { get; set; } = null!;
    /// <summary>Current absolute path under the managed root (updated on quarantine rename).</summary>
    public string Path { get; set; } = null!;
    public string Status { get; set; } = OrphanCandidateStatuses.Candidate;
    /// <summary>First observation — the grace-period anchor (≥24 h default).</summary>
    public DateTime FirstSeenAt { get; set; }
    public DateTime? QuarantinedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
    /// <summary>Why it was a candidate (workflow row missing / storage marked deleted) + last observation facts.</summary>
    public string? Details { get; set; }
    public DateTime UpdatedAt { get; set; }
}
