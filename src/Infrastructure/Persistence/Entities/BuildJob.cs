namespace PiAgent.Infrastructure.Persistence;

/// <summary>Orchestrator-side BuildJob dispatch/polling states (02 §12b, decision 81). Distinct from the
/// AgentProfileVersion build state and from the Build Worker's local states: `failed` here means only
/// "terminal build failure recorded" (the version flip to `build_failed` happens in the same write).</summary>
public static class BuildJobStates
{
    /// <summary>Created (buildId persisted); dispatch not yet accepted by the Build Worker or retry due.</summary>
    public const string Pending = "pending";
    /// <summary>`POST /internal/builds` answered 202; the Orchestrator polls `GET /internal/builds/{buildId}`.</summary>
    public const string Polling = "polling";
    /// <summary>Validated success (tag/ID/log reference recorded; mirrors into AgentProfileVersion).</summary>
    public const string Completed = "completed";
    /// <summary>Terminal build failure polled from the Worker; version becomes `build_failed`.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// Durable record of one AgentProfileVersion image build; created by the Orchestrator BEFORE first
/// dispatch (02 §12b, decision 81). One BuildJob per AgentProfileVersion (unique index); retries and
/// the restart reconciler always reuse the same `Id` — the Build Worker is idempotent by `buildId`.
/// The Orchestrator owns this row and the version state; the Worker never touches this table.
/// </summary>
public class BuildJob
{
    /// <summary>The unique `buildId`, generated and persisted before dispatch.</summary>
    public Guid Id { get; set; }
    public Guid AgentProfileVersionId { get; set; }
    /// <summary>Lineage record of the dispatch contract (04 §6): buildId, profile + version, source commit
    /// SHA, configuration snapshot identifier and build context reference. The full snapshot stays on the
    /// pinned version row; this payload records what a dispatch carried.</summary>
    public string DispatchPayload { get; set; } = null!;
    public string State { get; set; } = null!;
    /// <summary>Consecutive transient failures (dispatch/poll); reset on any successful contact.</summary>
    public int Attempts { get; set; }
    /// <summary>Exponential-backoff bookkeeping: transient errors defer the next dispatch/poll attempt.</summary>
    public DateTime? NextRetryAt { get; set; }
    /// <summary>Validated result, recorded on completion (02 §12b); `image_digest` mirrors into the version row.</summary>
    public string? ImageTag { get; set; }
    public string? ImageDigest { get; set; }
    public string? BuildLogReference { get; set; }
    /// <summary>Last transient error or terminal failure reason (operators' first diagnose stop; §8 error shape mirrors AgentCommand/OrchestrationJob rows).</summary>
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
