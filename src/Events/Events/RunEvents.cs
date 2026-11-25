namespace PiAgent.Events;

/// <summary>events.md "Run lifecycle events" — each transition is an event (docs/run-worker.md).</summary>

/// <summary>Pointers-only run input (ADR-0006: agents read GitHub themselves).</summary>
public sealed record RunInput(
    string Owner,
    string Repo,
    long ItemNumber,
    long? PrNumber,
    string Command,
    long PlanCommentId,
    string Profile) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>Orchestrator, after admitting any event — the Run is queued.</summary>
public sealed record RunScheduled(long RunId, long TaskId, long WorkflowId, RunInput Input) : IEvent
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

/// <summary>Run settled WITH report_completion — next flow stage or waiting_for_human.</summary>
public sealed record RunCompleted(long RunId, long TaskId, string Owner, string Repo, long ItemNumber) : IEvent
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
/// never dropped; heartbeat/progress routes are recorded but carry no state.
/// </summary>
public sealed record AgentCallback(long RunId, string Route, string? Body) : IEvent
{
    public string StreamKey => Streams.Global;
}
