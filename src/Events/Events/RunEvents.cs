namespace PiAgent.Events;

/// <summary>events.md "Run lifecycle events" — each transition is an event (docs/run-worker.md).</summary>

/// <summary>
/// Pointers-only run input v2 (ADR-0006: agents read GitHub themselves;
/// docs/run-worker.md "Run input (pointers v2)"). The Orchestrator fills every pointer
/// from Workflow/Task state at run_scheduled; the worker exports the entry-facing subset
/// as container env and the Bridge renders the task prompt from the same file.
/// </summary>
public sealed record RunInput(
    string Owner,
    string Repo,
    long ItemNumber,
    long? PrNumber,
    string Command,
    long PlanCommentId,
    string Profile,
    string? AgentBranch,
    string DefaultBranch,
    string? ReviewHeadSha) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>
/// Orchestrator, after admitting any event — the Run is queued. Secrets carries the
/// profile's secret NAMES (config plane `profile_secrets`, the frozen profile.yaml
/// `secrets:` list): the worker binds the host-managed files read-only at
/// `/run/secrets/<name>` — names travel through configuration, values never do.
/// </summary>
public sealed record RunScheduled(
    long RunId, long TaskId, long WorkflowId, RunInput Input,
    IReadOnlyList<string>? Secrets = null) : IEvent
{
    public string StreamKey => Input.StreamKey;
}

/// <summary>Run worker, on claiming the Run — the agent Run starts.</summary>
public sealed record RunProcessing(long RunId, long TaskId, string Owner, string Repo, long ItemNumber) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>Orchestrator, on the cancel path — the run worker stops the container.</summary>
public sealed record RunStopRequested(long RunId, string Owner, string Repo, long ItemNumber, string Reason) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>
/// Run settled WITH report_completion — next flow stage or waiting_for_human. Branch and
/// CommitSha are the completed callback's git facts (docs/run-worker.md terminal payload);
/// the Orchestrator records them as Workflow pointers (the agent branch a fix round
/// continues, the head commit a review round pins).
/// </summary>
public sealed record RunCompleted(
    long RunId,
    long TaskId,
    string Owner,
    string Repo,
    long ItemNumber,
    string? Branch = null,
    string? CommitSha = null) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>
/// Run settled WITHOUT report_completion, never started, crashed, or watchdog timeout —
/// auto-retry or waiting_for_human, the Orchestrator decides.
/// </summary>
public sealed record RunFailed(long RunId, long TaskId, string Owner, string Repo, long ItemNumber, string Cause) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>Run worker stopped the Run for run_stop_requested — Task → cancelled.</summary>
public sealed record RunCancelled(long RunId, long TaskId, string Owner, string Repo, long ItemNumber) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>
/// The Bridge's egress (stage 4): one POST /events per callback — terminal routes are
/// never dropped; heartbeat/progress routes are recorded but carry no state. EventId is
/// the Bridge's deterministic callback id (run + toolCallId), promoted to the row's
/// payload for the ingestion dedup index (docs/run-worker.md route tool).
/// </summary>
public sealed record AgentCallback(long RunId, string Route, string? Body, string? EventId = null) : IEvent
{
    public string StreamKey => Streams.Global;
}
