namespace PiAgent.Infrastructure.Persistence;

/// <summary>Run machine states. `stopping` is the awaiting-confirmed-stop state;
/// `timed_out` is the `max_run_duration` terminal.</summary>
public static class RunStatuses
{
    public const string Pending = "pending";
    public const string Starting = "starting";
    public const string Running = "running";
    public const string Stopping = "stopping";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string TimedOut = "timed_out";
}

/// <summary>Canonical Run error codes (the vocabulary the Bridge reports and the
/// Orchestrator consumes; the set that drives e.g. the failure notification).</summary>
public static class RunErrorCodes
{
    public const string GithubPublicationFailed = "github_publication_failed";
    public const string RuntimeFailure = "runtime_failure";
    public const string ArtifactTooLarge = "artifact_too_large";
    /// <summary>The Bridge's publication toolcheck: a mandated publication kind stayed unconfirmed
    /// after the settle/nudge rounds (the agent never self-reports — there is no result manifest).</summary>
    public const string PublicationMissing = "publication_missing";
    // The three execution-limit codes + the permission denial.
    public const string StartupTimeout = "startup_timeout";
    public const string IdleTimeout = "idle_timeout";
    public const string RequiredPermissionDenied = "required_permission_denied";

    /// <summary>Transient pre-execution infrastructure codes: automatic retry
    /// applies only to these and only when the Run never began executing (no `agent_started` event).
    /// `startup_timeout` is pre-execution by definition; `runtime_failure` is retried pre-execution only
    /// (a container death before the agent started — RunReconciler), never after execution began.</summary>
    public static readonly string[] TransientPreExecution = [StartupTimeout, RuntimeFailure];
}

/// <summary>
/// One execution attempt of a Task; a retry creates a new attempt with `attempt_number + 1`.
/// The Run↔AgentSession and Run↔Container links live on AgentSession.RunId (unique) and
/// ContainerInstance.RunId respectively (no circular FK pairs). Terminated only via the
/// Runtime callbacks — terminal Run state + confirmed stop release the capacity slot.
/// </summary>
public class Run
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    /// <summary>Assigned by the Orchestrator; agents never change it.</summary>
    public int AttemptNumber { get; set; }
    public string Status { get; set; } = null!;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? LastHeartbeatAt { get; set; }
    public DateTime? LastProgressAt { get; set; }
    public int? ExitCode { get; set; }
    /// <summary>Canonical error codes (e.g. `startup_timeout`, `github_publication_failed`).</summary>
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>Short per-attempt diagnostic for /status and notifications; full results live in Artifacts.</summary>
    public string? ResultSummary { get; set; }
    /// <summary>Points at the per-Run log directory on host storage; content never in PostgreSQL.</summary>
    public string? LogStorageReference { get; set; }
    public long? LogSizeBytes { get; set; }
    public DateTime? LogsCompletedAt { get; set; }
    public DateTime? LogsDeletedAt { get; set; }

    /// <summary>Stop-signal timeline (reconciliation hook): when the Runtime API recorded
    /// the Orchestrator's /stop request; null until then. Set with `stop_reason` in one update.</summary>
    public DateTime? StopRequestedAt { get; set; }
    /// <summary>Free-form reason delivered with the stop request (e.g. `stop_requested`).</summary>
    public string? StopReason { get; set; }
}
