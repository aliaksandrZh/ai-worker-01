using Microsoft.Extensions.Logging;
using PiAgent.Events;
using PiAgent.Orchestration.State;

namespace PiAgent.Orchestration.Consumers;

/// <summary>Commands, replies, decisions, cancellation, closure — the admission side.</summary>
public sealed class OrchestratorConsumer(Orchestrator core) : IEventConsumer
{
    public Type[] Consumes { get; } =
    [
        typeof(UserCommand), typeof(UserReply), typeof(UserDecision), typeof(UserCancel),
        typeof(ItemClosed), typeof(CommentChanged),
    ];

    public string Name => "OrchestratorCommands";

    public Task HandleAsync(IEvent evt, int attempt, CancellationToken ct) => evt switch
    {
        UserCommand command => core.HandleUserCommandAsync(command, ct),
        UserReply reply => core.HandleUserReplyAsync(reply, ct),
        UserDecision decision => core.HandleUserDecisionAsync(decision, ct),
        UserCancel cancel => core.HandleUserCancelAsync(cancel, ct),
        ItemClosed closed => core.HandleItemClosedAsync(closed, ct),
        // a non-command comment on an item with a parked Task is the User's reply:
        // commands were already derived by the Poller (docs/github-sync.md)
        CommentChanged comment => core.HandleCommentChangedAsync(comment, ct),
        _ => Task.CompletedTask,
    };
}

/// <summary>Run lifecycle: the state machine's spine (docs/orchestrator.md §3).</summary>
public sealed class RunLifecycleConsumer(Orchestrator core) : IEventConsumer
{
    public Type[] Consumes { get; } =
    [
        typeof(RunProcessing), typeof(RunCompleted), typeof(RunFailed), typeof(RunCancelled),
    ];

    public string Name => "OrchestratorRuns";

    public Task HandleAsync(IEvent evt, int attempt, CancellationToken ct) => evt switch
    {
        RunProcessing processing => core.HandleRunProcessingAsync(processing, ct),
        RunCompleted completed => core.HandleRunCompletedAsync(completed, ct),
        RunFailed failed => core.HandleRunFailedAsync(failed, ct),
        RunCancelled cancelled => core.HandleRunCancelledAsync(cancelled, ct),
        _ => Task.CompletedTask,
    };
}

/// <summary>Agent tool events + board-card links (the flow's mid-flight beats).</summary>
public sealed class WorkflowEventsConsumer(Orchestrator core, ILogger<WorkflowEventsConsumer> logger) : IEventConsumer
{
    public Type[] Consumes { get; } =
    [
        typeof(PlanSaved), typeof(BlockerReported), typeof(ReviewDecisionRequested),
        typeof(EnvIssueReported), typeof(CriteriaClarificationRequested),
        typeof(ReviewCommentsSubmitted), typeof(BugReported), typeof(TestsPassed),
        typeof(ProjectItemChanged), typeof(LabelsChanged), typeof(PrUpdated),
    ];

    public string Name => "OrchestratorWorkflow";

    public Task HandleAsync(IEvent evt, int attempt, CancellationToken ct) => evt switch
    {
        PlanSaved plan => core.HandlePlanSavedAsync(plan, ct),
        ReviewCommentsSubmitted comments => core.HandleReviewCommentsSubmittedAsync(comments, ct),
        BugReported bug => core.HandleBugReportedAsync(bug, ct),
        TestsPassed tests => core.HandleTestsPassedAsync(tests, ct),
        ProjectItemChanged card => core.HandleProjectItemChangedAsync(card, ct),
        BlockerReported b => core.HandleEscalationAsync(b,
            $"blocked: {b.Description}\n\n{string.Join('\n', b.Options.Select(o => $"- {o}"))}", ct),
        ReviewDecisionRequested r => core.HandleEscalationAsync(r,
            $"decision needed: {r.Issue}\n\n{string.Join('\n', r.Options.Select(o => $"- {o}"))}", ct),
        EnvIssueReported e => core.HandleEscalationAsync(e,
            $"environment issue: {e.Description} — needs: {e.WhatIsNeeded}", ct),
        CriteriaClarificationRequested c => core.HandleEscalationAsync(c,
            $"acceptance criteria need clarification: {c.Question}", ct),
        LabelsChanged labels => IgnoreLabelTriggersAsync(labels, ct),
        PrUpdated updated => core.HandlePrUpdatedAsync(updated, ct),
        _ => Task.CompletedTask,
    };

    // Board/label auto-triggers (auto_plan, auto_code, auto_review, auto_fix) are a
    // later stage: the event arrives, nothing consumes it yet — recorded, not dropped.
    private Task IgnoreLabelTriggersAsync(LabelsChanged labels, CancellationToken ct)
    {
        logger.LogInformation("labels changed on {item}: [{labels}] (auto-triggers not wired yet)",
            labels.StreamKey, string.Join(", ", labels.Labels));
        return Task.CompletedTask;
    }
}
