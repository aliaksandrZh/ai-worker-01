namespace PiAgent.Events;

/// <summary>events.md "Run lifecycle events" — each transition is an event (docs/run-worker.md).</summary>

/// <summary>
/// Pointers-only run input v2 (ADR-0006: agents read GitHub themselves;
/// docs/run-worker.md "Run input (pointers v2)"). The Orchestrator fills every pointer
/// from Workflow/Task state at run_scheduled; the worker exports the entry-facing subset
/// as container env and the Bridge renders the task prompt from the same file.
/// SessionId is the Orchestrator's globally unique pi session id (docs/run-session-id.md):
/// it scopes the Bridge's terminal eventIds and the worker's callback guard. Null on rows
/// created before the field existed — the worker falls back to the run id.
/// </summary>
public sealed record RunInput : RepositoryEvent
{
    public required long ItemNumber { get; init; }
    public required long? PrNumber { get; init; }
    public required string Command { get; init; }
    public required long PlanCommentId { get; init; }
    public required string Profile { get; init; }
    public required string? AgentBranch { get; init; }
    public required string DefaultBranch { get; init; }
    public required string? ReviewHeadSha { get; init; }
    public string? SessionId { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>
/// Orchestrator, after admitting any event — the Run is queued. Secrets carries the
/// profile's secret NAMES (config plane `profile_secrets`, the frozen profile.yaml
/// `secrets:` list): the worker binds the host-managed files read-only at
/// `/run/secrets/<name>` — names travel through configuration, values never do.
/// </summary>
public sealed record RunScheduled : RootEvent
{
    public required RunInput Input { get; init; }
    public IReadOnlyList<string>? Secrets { get; init; }

    public override string StreamKey => Input.StreamKey;
}

/// <summary>Run worker, on claiming the Run — the agent Run starts.</summary>
public sealed record RunProcessing : RepositoryEvent
{
    public required long ItemNumber { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>Orchestrator, on the cancel path — the run worker stops the container.</summary>
public sealed record RunStopRequested : RepositoryEvent
{
    public required long ItemNumber { get; init; }
    public required string Reason { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>
/// Run settled WITH report_completion — next flow stage or waiting_for_human. Branch and
/// CommitSha are the completed callback's git facts (docs/run-worker.md terminal payload);
/// the Orchestrator records them as Workflow pointers (the agent branch a fix round
/// continues, the head commit a review round pins).
/// </summary>
public sealed record RunCompleted : RepositoryEvent
{
    public required long ItemNumber { get; init; }
    public string? Branch { get; init; }
    public string? CommitSha { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>
/// Run settled WITHOUT report_completion, never started, crashed, or watchdog timeout —
/// auto-retry or waiting_for_human, the Orchestrator decides.
/// </summary>
public sealed record RunFailed : RepositoryEvent
{
    public required long ItemNumber { get; init; }
    public required string Cause { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>Run worker stopped the Run for run_stop_requested — Task → cancelled.</summary>
public sealed record RunCancelled : RepositoryEvent
{
    public required long ItemNumber { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>
/// The Bridge's egress (stage 4): one POST /events per callback — terminal routes are
/// never dropped; heartbeat/progress routes are recorded but carry no state. EventId is
/// the Bridge's deterministic callback id (run + toolCallId), promoted to the row's
/// payload for the ingestion dedup index (docs/run-worker.md route tool).
/// </summary>
public sealed record AgentCallback : RootEvent
{
    public required string Route { get; init; }
    public required string? Body { get; init; }

    public override string StreamKey => Streams.Global;
}
