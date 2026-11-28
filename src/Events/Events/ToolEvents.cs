namespace PiAgent.Events;

using System.Text.Json;

/// <summary>events.md "Agent tool events" — every communication tool call produces exactly one event.</summary>

/// <summary>save_plan → the plan is published as an issue comment, approval step runs.</summary>
public sealed record PlanSaved : RepositoryEvent
{
    public required long ItemNumber { get; init; }
    public required string Goals { get; init; }
    public required string Scope { get; init; }
    public required string AcceptanceCriteria { get; init; }
    public required JsonElement? Steps { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>request_clarification → waiting_for_human; the answer routes back to the Planner.</summary>
public sealed record ClarificationRequested : RepositoryEvent
{
    public required long ItemNumber { get; init; }
    public required string Question { get; init; }
    public required string? Context { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>report_blocker → waiting_for_human.</summary>
public sealed record BlockerReported : RepositoryEvent
{
    public required long ItemNumber { get; init; }
    public required string Description { get; init; }
    public required string Cause { get; init; }
    public required IReadOnlyList<string> Options { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>submit_review_comments → published to the PR, fixing Coder Run starts.</summary>
public sealed record ReviewCommentsSubmitted : RepositoryEvent
{
    public required long ItemNumber { get; init; }
    public required long PrNumber { get; init; }
    public required IReadOnlyList<ReviewFinding> Comments { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>One review finding (docs/flows tools.md submit_review_comments signature).</summary>
public sealed record ReviewFinding(string File, int Line, string Severity, string Message);

/// <summary>request_decision → waiting_for_human.</summary>
public sealed record ReviewDecisionRequested : RepositoryEvent
{
    public required long ItemNumber { get; init; }
    public required string Issue { get; init; }
    public required string Context { get; init; }
    public required IReadOnlyList<string> Options { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>report_bug → published to the issue, fix assigned to the Coder.</summary>
public sealed record BugReported : RepositoryEvent
{
    public required long ItemNumber { get; init; }
    public required string Summary { get; init; }
    public required IReadOnlyList<string> ReproSteps { get; init; }
    public required string TestResults { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>report_env_issue → waiting_for_human until the environment is restored.</summary>
public sealed record EnvIssueReported : RepositoryEvent
{
    public required long ItemNumber { get; init; }
    public required string Description { get; init; }
    public required string WhatIsNeeded { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>request_criteria_clarification → waiting_for_human; answer routed to the Planner.</summary>
public sealed record CriteriaClarificationRequested : RepositoryEvent
{
    public required long ItemNumber { get; init; }
    public required string Question { get; init; }
    public required string? Context { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>report_tests_passed → manual acceptance policy check.</summary>
public sealed record TestsPassed : RepositoryEvent
{
    public required long ItemNumber { get; init; }
    public required string Summary { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>
/// submit_pr_description → the Coder's PR title/body carrier (publication rule: the
/// Orchestrator creates the PR after run_completed — a PR cannot exist before its head
/// branch, so the number is known only for later updates).
/// </summary>
public sealed record PrDescriptionSubmitted : RepositoryEvent
{
    public required long ItemNumber { get; init; }
    public required long? PrNumber { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }

    public override string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

// ---- payload records (ticket 01 wire contract) ---------------------------------------------
// What the agent sends as the call's args — no Owner/Repo/ItemNumber envelope; the Run
// Worker's tool-event adapter (docs/run-worker.md route tool) adds the envelope from the
// Run context and appends the typed event to the root item's stream. Required members
// make a form that does not match the contract shape deterministic garbage at the seatbelt.

/// <summary>save_plan args (docs/flows tools.md signature).</summary>
public sealed record PlanPayload
{
    public required string Goals { get; init; }
    public required string Scope { get; init; }
    public required string AcceptanceCriteria { get; init; }
    /// <summary>Raw steps array — the planner may send strings or structured steps; the plan
    /// comment is published from Goals/Scope/AcceptanceCriteria, steps ride for audit.</summary>
    public JsonElement? Steps { get; init; }
}

/// <summary>request_clarification / request_criteria_clarification args (shared question form).</summary>
public sealed record ClarificationPayload
{
    public required string Question { get; init; }
    public string? Context { get; init; }
}

/// <summary>report_blocker args (what happened + causes + options).</summary>
public sealed record BlockerPayload
{
    public required string Description { get; init; }
    public required string Cause { get; init; }
    public required IReadOnlyList<string> Options { get; init; }
}

/// <summary>request_decision args (issue + context + options).</summary>
public sealed record DecisionPayload
{
    public required string Issue { get; init; }
    public required string Context { get; init; }
    public required IReadOnlyList<string> Options { get; init; }
}

/// <summary>submit_review_comments args.</summary>
public sealed record ReviewComments
{
    public required IReadOnlyList<ReviewFinding> Comments { get; init; }
}

/// <summary>report_bug args.</summary>
public sealed record BugPayload
{
    public required string Summary { get; init; }
    public required IReadOnlyList<string> ReproSteps { get; init; }
    public required string TestResults { get; init; }
}

/// <summary>report_env_issue args.</summary>
public sealed record EnvIssuePayload
{
    public required string Description { get; init; }
    public required string WhatIsNeeded { get; init; }
}

/// <summary>submit_pr_description args.</summary>
public sealed record PrDescription
{
    public required string Title { get; init; }
    public required string Body { get; init; }
}

/// <summary>report_tests_passed args.</summary>
public sealed record TestsSummary
{
    public required string Summary { get; init; }
}
