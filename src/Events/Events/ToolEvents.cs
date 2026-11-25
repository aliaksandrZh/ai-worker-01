namespace PiAgent.Events;

/// <summary>events.md "Agent tool events" — every communication tool call produces exactly one event.</summary>

/// <summary>save_plan → the plan is published as an issue comment, approval step runs.</summary>
public sealed record PlanSaved(
    string Owner,
    string Repo,
    long ItemNumber,
    long WorkflowId,
    string Goals,
    string Scope,
    string AcceptanceCriteria) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>report_blocker → waiting_for_human.</summary>
public sealed record BlockerReported(
    string Owner,
    string Repo,
    long ItemNumber,
    long RunId,
    string Description,
    string Cause,
    IReadOnlyList<string> Options) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>submit_review_comments → published to the PR, fixing Coder Run starts.</summary>
public sealed record ReviewCommentsSubmitted(
    string Owner,
    string Repo,
    long ItemNumber,
    long PrNumber,
    long RunId,
    IReadOnlyList<ReviewFinding> Comments) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>One review finding (docs/flows tools.md submit_review_comments signature).</summary>
public sealed record ReviewFinding(string File, int Line, string Severity, string Message);

/// <summary>request_decision → waiting_for_human.</summary>
public sealed record ReviewDecisionRequested(
    string Owner,
    string Repo,
    long ItemNumber,
    long RunId,
    string Issue,
    string Context,
    IReadOnlyList<string> Options) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>report_bug → published to the issue, fix assigned to the Coder.</summary>
public sealed record BugReported(
    string Owner,
    string Repo,
    long ItemNumber,
    long TaskId,
    string Summary,
    IReadOnlyList<string> ReproSteps,
    string TestResults) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>report_env_issue → waiting_for_human until the environment is restored.</summary>
public sealed record EnvIssueReported(
    string Owner,
    string Repo,
    long ItemNumber,
    long TaskId,
    string Description,
    string WhatIsNeeded) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>request_criteria_clarification → waiting_for_human; answer routed to the Planner.</summary>
public sealed record CriteriaClarificationRequested(
    string Owner,
    string Repo,
    long ItemNumber,
    long TaskId,
    string Question,
    string Context) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}

/// <summary>report_tests_passed → manual acceptance policy check.</summary>
public sealed record TestsPassed(
    string Owner,
    string Repo,
    long ItemNumber,
    long TaskId,
    string Summary) : IEvent
{
    public string StreamKey => new ItemRef(Owner, Repo, ItemNumber).Key;
}
