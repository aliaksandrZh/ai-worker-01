using PiAgent.Events;

namespace PiAgent.Orchestration.State;

/// <summary>
/// One orchestration transaction: orchestration state changes and the events that
/// announce them commit atomically (docs/eventlog.md invariant 6). Created via
/// IOrchestrationState.BeginAsync.
/// </summary>
public interface IOrchestrationUnitOfWork : IAsyncDisposable
{
    IEventTransaction Events { get; }
}

public interface IOrchestrationUnitOfWorkFactory
{
    Task<IOrchestrationUnitOfWork> BeginAsync(CancellationToken ct);
}

/// <summary>
/// Orchestration state (Workflow/Task/Run rows). Every operation participates in the
/// unit of work passed to it — the atomicity unit is one event reaction.
/// </summary>
public interface IOrchestrationState
{
    IOrchestrationUnitOfWorkFactory Factory { get; }

    /// <summary>Existing Workflow for the item, without creating one.</summary>
    Task<WorkflowRow?> FindWorkflowAsync(ItemRef item, IOrchestrationUnitOfWork uow, CancellationToken ct);

    Task<WorkflowRow> GetOrCreateWorkflowAsync(ItemRef item, IOrchestrationUnitOfWork uow, CancellationToken ct);

    Task<WorkflowRow> GetWorkflowAsync(long workflowId, IOrchestrationUnitOfWork uow, CancellationToken ct);

    Task SetWorkflowStatusAsync(long workflowId, string status, IOrchestrationUnitOfWork uow, CancellationToken ct);

    Task SetWorkflowPlanAsync(long workflowId, bool approved, long planCommentId, IOrchestrationUnitOfWork uow, CancellationToken ct);

    Task SetWorkflowNodeAsync(long workflowId, string nodeId, IOrchestrationUnitOfWork uow, CancellationToken ct);

    /// <summary>
    /// Records the Workflow's run-input pointers: the agent branch a fix round continues
    /// (from the completed Coder run's git facts) and the head commit a review round pins
    /// (from the same facts or from pr_updated). Non-null values overwrite; null preserves.
    /// </summary>
    Task SetWorkflowPointersAsync(long workflowId, string? agentBranch, string? headSha, IOrchestrationUnitOfWork uow, CancellationToken ct);

    Task IncrementReviewRoundsAsync(long workflowId, IOrchestrationUnitOfWork uow, CancellationToken ct);

    Task<long> CreateTaskAsync(long workflowId, string type, IOrchestrationUnitOfWork uow, CancellationToken ct);

    Task<TaskRow?> GetActiveTaskAsync(long workflowId, IOrchestrationUnitOfWork uow, CancellationToken ct);

    Task<TaskRow> GetTaskAsync(long taskId, IOrchestrationUnitOfWork uow, CancellationToken ct);

    Task SetTaskStatusAsync(long taskId, string status, IOrchestrationUnitOfWork uow, CancellationToken ct);

    /// <summary>How many tasks of this type the Workflow already has (round caps).</summary>
    Task<int> CountTasksAsync(long workflowId, string type, IOrchestrationUnitOfWork uow, CancellationToken ct);

    Task<long> CreateRunAsync(long taskId, RunInput input, IOrchestrationUnitOfWork uow, CancellationToken ct);

    Task<RunRow> GetRunAsync(long runId, IOrchestrationUnitOfWork uow, CancellationToken ct);

    Task SetRunStatusAsync(long runId, string status, IOrchestrationUnitOfWork uow, CancellationToken ct);

    /// <summary>Runs of the Task still in flight (for the stop path).</summary>
    Task<IReadOnlyList<RunRow>> ListRunsAsync(long taskId, string status, IOrchestrationUnitOfWork uow, CancellationToken ct);
}
