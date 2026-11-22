namespace PiAgent.Infrastructure.Persistence;

/// <summary>Run machine states (03 §3). `stopping` is decision 75/61's awaiting-confirmed-stop state
/// (driven by ticket 14); `timed_out` is the `max_run_duration` terminal (ticket 13).</summary>
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

/// <summary>Canonical Run error codes from 04 §7 (the vocabulary the Bridge reports and the
/// Orchestrator consumes; the set that drives e.g. the decision-42 failure notification).</summary>
public static class RunErrorCodes
{
    public const string GithubPublicationFailed = "github_publication_failed";
    public const string RuntimeFailure = "runtime_failure";
    public const string ArtifactTooLarge = "artifact_too_large";
    public const string ManifestInvalid = "manifest_invalid";
    // Ticket 13 (04 §7): the three execution-limit codes + the decision-69 permission denial.
    public const string StartupTimeout = "startup_timeout";
    public const string IdleTimeout = "idle_timeout";
    public const string RequiredPermissionDenied = "required_permission_denied";

    /// <summary>Transient pre-execution infrastructure codes (decision 70, ticket 15): automatic retry
    /// applies only to these and only when the Run never began executing (no `agent_started` event).
    /// `startup_timeout` is pre-execution by definition; `runtime_failure` is retried pre-execution only
    /// (a container death before the agent started — RunReconciler), never after execution began.</summary>
    public static readonly string[] TransientPreExecution = [StartupTimeout, RuntimeFailure];
}

/// <summary>
/// One execution attempt of a Task (02 §14); a retry creates a new attempt with `attempt_number + 1`.
/// The Run↔AgentSession and Run↔Container links live on AgentSession.RunId (unique) and
/// ContainerInstance.RunId respectively (design doc: no circular FK pairs). Terminated only via the
/// Runtime callbacks — terminal Run state + confirmed stop release the capacity slot (decision 75).
/// </summary>
public class Run
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    /// <summary>Assigned by the Orchestrator; agents never change it (02 §14).</summary>
    public int AttemptNumber { get; set; }
    public string Status { get; set; } = null!;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? LastHeartbeatAt { get; set; }
    public DateTime? LastProgressAt { get; set; }
    public int? ExitCode { get; set; }
    /// <summary>Canonical error codes from 04 §7 (e.g. `startup_timeout`, `github_publication_failed`).</summary>
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>Short per-attempt diagnostic for /status and notifications; full results live in Artifacts (decision 79).</summary>
    public string? ResultSummary { get; set; }
    /// <summary>Points at the per-Run log directory on host storage (decision 43); content never in PostgreSQL.</summary>
    public string? LogStorageReference { get; set; }
    public long? LogSizeBytes { get; set; }
    public DateTime? LogsCompletedAt { get; set; }
    public DateTime? LogsDeletedAt { get; set; }

    /// <summary>Stop-signal timeline (decision 61, reconciliation hook): when the Runtime API recorded
    /// the Orchestrator's /stop request; null until then. Set with `stop_reason` in one update.</summary>
    public DateTime? StopRequestedAt { get; set; }
    /// <summary>Free-form reason delivered with the stop request (e.g. `stop_requested`).</summary>
    public string? StopReason { get; set; }
}
