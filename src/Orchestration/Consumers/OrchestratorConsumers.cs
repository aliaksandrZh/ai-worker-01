using Microsoft.Extensions.Logging;
using PiAgent.Events;
using PiAgent.Orchestration.State;

namespace PiAgent.Orchestration.Consumers;

/// <summary>Commands, decisions, cancellation, closure — the admission side.</summary>
public sealed class OrchestratorInbound(Orchestrator core) : IEventConsumer
{
    public Type[] Consumes { get; } =
    [
        typeof(UserCommand), typeof(UserDecision), typeof(UserCancel), typeof(ItemClosed),
    ];

    public string Name => "OrchestratorInbound";

    public Task HandleAsync(IEvent evt, int attempt, CancellationToken ct) => evt switch
    {
        UserCommand command => core.HandleUserCommandAsync(command, ct),
        UserDecision decision => core.HandleUserDecisionAsync(decision, ct),
        UserCancel cancel => core.HandleUserCancelAsync(cancel, ct),
        ItemClosed closed => core.HandleItemClosedAsync(closed, ct),
        _ => Task.CompletedTask,
    };
}

/// <summary>Run lifecycle: the state machine's spine (docs/orchestrator.md §3).</summary>
public sealed class OrchestratorRuns(Orchestrator core) : IEventConsumer
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
public sealed class OrchestratorAgent(Orchestrator core) : IEventConsumer
{
    public Type[] Consumes { get; } =
    [
        typeof(PlanSaved), typeof(ClarificationRequested), typeof(BlockerReported),
        typeof(ReviewDecisionRequested), typeof(EnvIssueReported), typeof(CriteriaClarificationRequested),
        typeof(ReviewCommentsSubmitted), typeof(BugReported), typeof(TestsPassed),
        typeof(ProjectItemChanged), typeof(PrUpdated), typeof(PrPublished),
    ];

    public string Name => "OrchestratorAgent";

    public Task HandleAsync(IEvent evt, int attempt, CancellationToken ct) => evt switch
    {
        PlanSaved plan => core.HandlePlanSavedAsync(plan, ct),
        ReviewCommentsSubmitted comments => core.HandleReviewCommentsSubmittedAsync(comments, ct),
        BugReported bug => core.HandleBugReportedAsync(bug, ct),
        TestsPassed tests => core.HandleTestsPassedAsync(tests, ct),
        ProjectItemChanged card => core.HandleProjectItemChangedAsync(card, ct),
        ClarificationRequested c => core.HandleEscalationAsync(c,
            $"clarification needed: {c.Question}", ct),
        BlockerReported b => core.HandleEscalationAsync(b,
            $"blocked: {b.Description}\n\n{string.Join('\n', b.Options.Select(o => $"- {o}"))}", ct),
        ReviewDecisionRequested r => core.HandleEscalationAsync(r,
            $"decision needed: {r.Issue}\n\n{string.Join('\n', r.Options.Select(o => $"- {o}"))}", ct),
        EnvIssueReported e => core.HandleEscalationAsync(e,
            $"environment issue: {e.Description} — needs: {e.WhatIsNeeded}", ct),
        CriteriaClarificationRequested c => core.HandleEscalationAsync(c,
            $"acceptance criteria need clarification: {c.Question}", ct),
        PrUpdated updated => core.HandlePrUpdatedAsync(updated, ct),
        PrPublished published => core.HandlePrPublishedAsync(published, ct),
        _ => Task.CompletedTask,
    };
}
