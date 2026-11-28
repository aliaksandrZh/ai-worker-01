using Microsoft.Extensions.Logging;
using PiAgent.Events;
using PiAgent.Orchestration.State;

namespace PiAgent.Orchestration.Consumers;

/// <summary>Commands, decisions, cancellation, closure — the admission side.</summary>
public sealed class OrchestratorInbound(Orchestrator core) : IEventConsumer
{
    public Type[] Consumes { get; } =
    [
        typeof(UserCommand), typeof(UserDecision), typeof(UserCancel), typeof(RepositoryClosed),
    ];

    public string Name => "OrchestratorInbound";

    public Task HandleAsync(IEvent evt, int attempt, long causationId, CancellationToken ct) => evt switch
    {
        UserCommand command => core.HandleUserCommandAsync(command, causationId, ct),
        UserDecision decision => core.HandleUserDecisionAsync(decision, causationId, ct),
        UserCancel cancel => core.HandleUserCancelAsync(cancel, causationId, ct),
        RepositoryClosed closed => core.HandleItemClosedAsync(closed, causationId, ct),
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

    public Task HandleAsync(IEvent evt, int attempt, long causationId, CancellationToken ct) => evt switch
    {
        RunProcessing processing => core.HandleRunProcessingAsync(processing, causationId, ct),
        RunCompleted completed => core.HandleRunCompletedAsync(completed, causationId, ct),
        RunFailed failed => core.HandleRunFailedAsync(failed, causationId, ct),
        RunCancelled cancelled => core.HandleRunCancelledAsync(cancelled, causationId, ct),
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
        typeof(ProjectItemChanged), typeof(PrUpdated), typeof(PrPublished), typeof(PublishFailed),
    ];

    public string Name => "OrchestratorAgent";

    public Task HandleAsync(IEvent evt, int attempt, long causationId, CancellationToken ct) => evt switch
    {
        PlanSaved plan => core.HandlePlanSavedAsync(plan, causationId, ct),
        ReviewCommentsSubmitted comments => core.HandleReviewCommentsSubmittedAsync(comments, causationId, ct),
        BugReported bug => core.HandleBugReportedAsync(bug, causationId, ct),
        TestsPassed tests => core.HandleTestsPassedAsync(tests, causationId, ct),
        ProjectItemChanged card => core.HandleProjectItemChangedAsync(card, causationId, ct),
        ClarificationRequested c => core.HandleEscalationAsync(c,
            $"clarification needed: {c.Question}", causationId, ct),
        BlockerReported b => core.HandleEscalationAsync(b,
            $"blocked: {b.Description}\n\n{string.Join('\n', b.Options.Select(o => $"- {o}"))}", causationId, ct),
        ReviewDecisionRequested r => core.HandleEscalationAsync(r,
            $"decision needed: {r.Issue}\n\n{string.Join('\n', r.Options.Select(o => $"- {o}"))}", causationId, ct),
        EnvIssueReported e => core.HandleEscalationAsync(e,
            $"environment issue: {e.Description} — needs: {e.WhatIsNeeded}", causationId, ct),
        CriteriaClarificationRequested c => core.HandleEscalationAsync(c,
            $"acceptance criteria need clarification: {c.Question}", causationId, ct),
        PrUpdated updated => core.HandlePrUpdatedAsync(updated, causationId, ct),
        PrPublished published => core.HandlePrPublishedAsync(published, causationId, ct),
        PublishFailed failed => core.HandlePublishFailedAsync(failed, causationId, ct),
        _ => Task.CompletedTask,
    };
}
