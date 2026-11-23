namespace PiAgent.Infrastructure.Persistence;

/// <summary>
/// Per-Run callback credential: a fresh random bearer token delivered to the
/// Bridge as a read-only secret file, stored only as a SHA-256 hash here, scoped to its single runId,
/// revoked after terminal delivery. Never an env var, never given to Pi, never logged.
/// </summary>
public class RunCredential
{
    public Guid Id { get; set; }
    /// <summary>The only run this credential may call back for (scoping).</summary>
    public Guid RunId { get; set; }
    /// <summary>sha256 hex of the token; presentation is hashed and compared — the raw token is never stored.</summary>
    public string TokenHash { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    /// <summary>Set after terminal delivery is confirmed; later callbacks are unauthorized.</summary>
    public DateTime? RevokedAt { get; set; }
}
