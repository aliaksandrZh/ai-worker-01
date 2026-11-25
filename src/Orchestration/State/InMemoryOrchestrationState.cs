using PiAgent.Events;

namespace PiAgent.Orchestration.State.InMemory;

/// <summary>
/// The second adapter at the orchestration-state seam: same transitions as the Postgres
/// store, no database. Tests drive reactions deterministically.
/// </summary>
public sealed class InMemoryOrchestrationState : IOrchestrationState
{
    private readonly List<WorkflowRow> _workflows = [];
    private readonly List<TaskRow> _tasks = [];
    private readonly List<RunRow> _runs = [];
    private long _workflowId;
    private long _taskId;
    private long _runId;

    public IOrchestrationUnitOfWorkFactory Factory { get; } = new InMemoryFactory();

    private sealed class InMemoryFactory : IOrchestrationUnitOfWorkFactory
    {
        public Task<IOrchestrationUnitOfWork> BeginAsync(CancellationToken ct) =>
            Task.FromResult<IOrchestrationUnitOfWork>(new InMemoryUnitOfWork());
    }

    private sealed class InMemoryUnitOfWork : IOrchestrationUnitOfWork
    {
        public IEventTransaction Events { get; } = new NoopEventTransaction();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopEventTransaction : IEventTransaction
    {
        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public Task<WorkflowRow?> FindWorkflowAsync(ItemRef item, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        var existing = _workflows.FirstOrDefault(w =>
            w.Owner == item.Owner && w.Repo == item.Repo && w.ItemNumber == item.Number);
        return Task.FromResult(existing);
    }

    public Task<WorkflowRow> GetOrCreateWorkflowAsync(ItemRef item, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        var existing = _workflows.FirstOrDefault(w =>
            w.Owner == item.Owner && w.Repo == item.Repo && w.ItemNumber == item.Number);
        if (existing is not null)
            return Task.FromResult(existing);

        var row = new WorkflowRow(++_workflowId, item.Owner, item.Repo, item.Number,
            WorkflowStatuses.Active, NodeId: null, PlanApproved: false, ReviewRounds: 0);
        _workflows.Add(row);
        return Task.FromResult(row);
    }

    public Task<WorkflowRow> GetWorkflowAsync(long workflowId, IOrchestrationUnitOfWork uow, CancellationToken ct) =>
        Task.FromResult(_workflows.First(w => w.Id == workflowId));

    public Task SetWorkflowStatusAsync(long workflowId, string status, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        var index = _workflows.FindIndex(w => w.Id == workflowId);
        _workflows[index] = _workflows[index] with { Status = status };
        return Task.CompletedTask;
    }

    public Task SetWorkflowPlanAsync(long workflowId, bool approved, long planCommentId, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        var index = _workflows.FindIndex(w => w.Id == workflowId);
        _workflows[index] = _workflows[index] with { PlanApproved = approved };
        return Task.CompletedTask;
    }

    public Task SetWorkflowNodeAsync(long workflowId, string nodeId, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        var index = _workflows.FindIndex(w => w.Id == workflowId);
        _workflows[index] = _workflows[index] with { NodeId = nodeId };
        return Task.CompletedTask;
    }

    public Task IncrementReviewRoundsAsync(long workflowId, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        var index = _workflows.FindIndex(w => w.Id == workflowId);
        _workflows[index] = _workflows[index] with { ReviewRounds = _workflows[index].ReviewRounds + 1 };
        return Task.CompletedTask;
    }

    public Task<long> CreateTaskAsync(long workflowId, string type, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        var seq = _tasks.Count(t => t.WorkflowId == workflowId) + 1;
        var row = new TaskRow(++_taskId, workflowId, seq, type, TaskStatuses.Queued);
        _tasks.Add(row);
        return Task.FromResult(row.Id);
    }

    public Task<TaskRow?> GetActiveTaskAsync(long workflowId, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        var active = _tasks
            .Where(t => t.WorkflowId == workflowId && OccupiedTaskStatuses.Set.Contains(t.Status))
            .OrderBy(t => t.Id)
            .FirstOrDefault();
        return Task.FromResult(active);
    }

    public Task<TaskRow> GetTaskAsync(long taskId, IOrchestrationUnitOfWork uow, CancellationToken ct) =>
        Task.FromResult(_tasks.First(t => t.Id == taskId));

    public Task SetTaskStatusAsync(long taskId, string status, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        var index = _tasks.FindIndex(t => t.Id == taskId);
        _tasks[index] = _tasks[index] with { Status = status };
        return Task.CompletedTask;
    }

    public Task<int> CountTasksAsync(long workflowId, string type, IOrchestrationUnitOfWork uow, CancellationToken ct) =>
        Task.FromResult(_tasks.Count(t => t.WorkflowId == workflowId && t.Type == type));

    public Task<long> CreateRunAsync(long taskId, RunInput input, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        var row = new RunRow(++_runId, taskId, RunStatuses.Scheduled, input);
        _runs.Add(row);
        return Task.FromResult(row.Id);
    }

    public Task<RunRow> GetRunAsync(long runId, IOrchestrationUnitOfWork uow, CancellationToken ct) =>
        Task.FromResult(_runs.First(r => r.Id == runId));

    public Task SetRunStatusAsync(long runId, string status, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        var index = _runs.FindIndex(r => r.Id == runId);
        _runs[index] = _runs[index] with { Status = status };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RunRow>> ListRunsAsync(long taskId, string status, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        IReadOnlyList<RunRow> runs = _runs.Where(r => r.TaskId == taskId && r.Status == status).ToList();
        return Task.FromResult(runs);
    }
}
