using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using PiAgent.Events;
using PiAgent.Events.Postgres;

namespace PiAgent.Orchestration.State.Postgres;

/// <summary>
/// Orchestration state in Postgres (raw SQL, snake_case). Shares the unit-of-work
/// transaction with the EventLog store so a state change and its announcing events
/// commit atomically. Idempotent DDL at startup — the formal baseline lands with the
/// stage-3 schema reset (docs/adr/0004).
/// </summary>
public sealed class PostgresOrchestrationState : IOrchestrationState
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource;
    private readonly EventQueueStore _eventStore;
    private readonly ILogger<PostgresOrchestrationState> _logger;

    public PostgresOrchestrationState(
        NpgsqlDataSource dataSource, EventQueueStore eventStore, ILogger<PostgresOrchestrationState> logger)
    {
        _dataSource = dataSource;
        _eventStore = eventStore;
        _logger = logger;
    }

    public IOrchestrationUnitOfWorkFactory Factory => _factory ??= new PgFactory(this);

    private IOrchestrationUnitOfWorkFactory? _factory;

    private sealed class PgFactory(PostgresOrchestrationState owner) : IOrchestrationUnitOfWorkFactory
    {
        public async Task<IOrchestrationUnitOfWork> BeginAsync(CancellationToken ct)
        {
            var conn = await owner._dataSource.OpenConnectionAsync(ct);
            var tx = await conn.BeginTransactionAsync(ct);
            return new PgUnitOfWork(owner._eventStore.WrapTransaction(conn, tx), conn, tx);
        }
    }

    private sealed class PgUnitOfWork(IEventTransaction events, NpgsqlConnection connection, NpgsqlTransaction transaction)
        : IOrchestrationUnitOfWork
    {
        public IEventTransaction Events { get; } = events;
        public NpgsqlConnection Connection { get; } = connection;
        public NpgsqlTransaction Transaction { get; } = transaction;

        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    /// <summary>Idempotent bootstrap DDL; safe to call on every host start.</summary>
    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS orch_workflow (
                id             bigserial PRIMARY KEY,
                owner          text   NOT NULL,
                repo           text   NOT NULL,
                item_number    bigint NOT NULL,
                node_id        text,
                status         text   NOT NULL,
                plan_approved  boolean NOT NULL DEFAULT false,
                review_rounds  int    NOT NULL DEFAULT 0,
                created_at     timestamptz NOT NULL DEFAULT now(),
                UNIQUE (owner, repo, item_number)
            );
            CREATE TABLE IF NOT EXISTS orch_task (
                id          bigserial PRIMARY KEY,
                workflow_id bigint NOT NULL REFERENCES orch_workflow,
                seq         int    NOT NULL,
                type        text   NOT NULL,
                status      text   NOT NULL,
                created_at  timestamptz NOT NULL DEFAULT now(),
                UNIQUE (workflow_id, seq)
            );
            CREATE TABLE IF NOT EXISTS orch_run (
                id         bigserial PRIMARY KEY,
                task_id    bigint NOT NULL REFERENCES orch_task,
                status     text   NOT NULL,
                input      jsonb  NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now()
            );
            """;
        await using var cmd = _dataSource.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("orchestration schema ensured");
    }

    private async Task<T> QueryRequiredAsync<T>(IOrchestrationUnitOfWork uow, string sql, Action<NpgsqlParameterCollection> bind,
        Func<NpgsqlDataReader, T> map, string missing, CancellationToken ct)
    {
        var connection = ((PgUnitOfWork)uow).Connection;
        await using var cmd = new NpgsqlCommand(sql, connection, ((PgUnitOfWork)uow).Transaction);
        bind(cmd.Parameters);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? map(reader)
            : throw new InvalidOperationException(missing);
    }

    private async Task<T?> QueryOptionalAsync<T>(IOrchestrationUnitOfWork uow, string sql, Action<NpgsqlParameterCollection> bind,
        Func<NpgsqlDataReader, T> map, CancellationToken ct) where T : class
    {
        var connection = ((PgUnitOfWork)uow).Connection;
        await using var cmd = new NpgsqlCommand(sql, connection, ((PgUnitOfWork)uow).Transaction);
        bind(cmd.Parameters);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? map(reader) : null;
    }

    private async Task ExecAsync(IOrchestrationUnitOfWork uow, string sql, Action<NpgsqlParameterCollection> bind, CancellationToken ct)
    {
        var connection = ((PgUnitOfWork)uow).Connection;
        await using var cmd = new NpgsqlCommand(sql, connection, ((PgUnitOfWork)uow).Transaction);
        bind(cmd.Parameters);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static WorkflowRow MapWorkflow(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3),
        r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetBoolean(6), r.GetInt32(7));

    public Task<WorkflowRow?> FindWorkflowAsync(ItemRef item, IOrchestrationUnitOfWork uow, CancellationToken ct) =>
        QueryOptionalAsync(uow,
            "SELECT id, owner, repo, item_number, status, node_id, plan_approved, review_rounds FROM orch_workflow WHERE owner = @owner AND repo = @repo AND item_number = @number",
            p =>
            {
                p.AddWithValue("owner", item.Owner);
                p.AddWithValue("repo", item.Repo);
                p.AddWithValue("number", item.Number);
            },
            MapWorkflow, ct);

    public Task<WorkflowRow> GetOrCreateWorkflowAsync(ItemRef item, IOrchestrationUnitOfWork uow, CancellationToken ct) =>
        QueryRequiredAsync(uow, """
            INSERT INTO orch_workflow (owner, repo, item_number, status)
            VALUES (@owner, @repo, @number, 'active')
            ON CONFLICT (owner, repo, item_number) DO UPDATE SET owner = EXCLUDED.owner
            RETURNING id, owner, repo, item_number, status, node_id, plan_approved, review_rounds
            """,
            p =>
            {
                p.AddWithValue("owner", item.Owner);
                p.AddWithValue("repo", item.Repo);
                p.AddWithValue("number", item.Number);
            },
            MapWorkflow, "workflow insert returned no row", ct);

    public Task<WorkflowRow> GetWorkflowAsync(long workflowId, IOrchestrationUnitOfWork uow, CancellationToken ct) =>
        QueryRequiredAsync(uow,
            "SELECT id, owner, repo, item_number, status, node_id, plan_approved, review_rounds FROM orch_workflow WHERE id = @id",
            p => p.AddWithValue("id", workflowId), MapWorkflow, $"workflow {workflowId} not found", ct);

    public Task SetWorkflowStatusAsync(long workflowId, string status, IOrchestrationUnitOfWork uow, CancellationToken ct) =>
        ExecAsync(uow, "UPDATE orch_workflow SET status = @status WHERE id = @id",
            p =>
            {
                p.AddWithValue("status", status);
                p.AddWithValue("id", workflowId);
            }, ct);

    public Task SetWorkflowPlanAsync(long workflowId, bool approved, long planCommentId, IOrchestrationUnitOfWork uow, CancellationToken ct) =>
        ExecAsync(uow, "UPDATE orch_workflow SET plan_approved = @approved WHERE id = @id",
            p =>
            {
                p.AddWithValue("approved", approved);
                p.AddWithValue("id", workflowId);
            }, ct);

    public Task SetWorkflowNodeAsync(long workflowId, string nodeId, IOrchestrationUnitOfWork uow, CancellationToken ct) =>
        ExecAsync(uow, "UPDATE orch_workflow SET node_id = @node WHERE id = @id",
            p =>
            {
                p.AddWithValue("node", nodeId);
                p.AddWithValue("id", workflowId);
            }, ct);

    public Task IncrementReviewRoundsAsync(long workflowId, IOrchestrationUnitOfWork uow, CancellationToken ct) =>
        ExecAsync(uow, "UPDATE orch_workflow SET review_rounds = review_rounds + 1 WHERE id = @id",
            p => p.AddWithValue("id", workflowId), ct);

    public async Task<long> CreateTaskAsync(long workflowId, string type, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        var connection = ((PgUnitOfWork)uow).Connection;
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO orch_task (workflow_id, seq, type, status)
            VALUES (@workflow, (SELECT COALESCE(MAX(seq), 0) + 1 FROM orch_task WHERE workflow_id = @workflow), @type, 'queued')
            RETURNING id
            """, connection, ((PgUnitOfWork)uow).Transaction);
        cmd.Parameters.AddWithValue("workflow", workflowId);
        cmd.Parameters.AddWithValue("type", type);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public Task<TaskRow?> GetActiveTaskAsync(long workflowId, IOrchestrationUnitOfWork uow, CancellationToken ct) =>
        QueryOptionalAsync(uow, """
            SELECT id, workflow_id, seq, type, status FROM orch_task
            WHERE workflow_id = @workflow AND status IN ('queued', 'processing', 'waiting_for_human')
            ORDER BY id LIMIT 1
            """,
            p => p.AddWithValue("workflow", workflowId),
            r => new TaskRow(r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.GetString(3), r.GetString(4)), ct);

    public Task<TaskRow> GetTaskAsync(long taskId, IOrchestrationUnitOfWork uow, CancellationToken ct) =>
        QueryRequiredAsync(uow, "SELECT id, workflow_id, seq, type, status FROM orch_task WHERE id = @id",
            p => p.AddWithValue("id", taskId),
            r => new TaskRow(r.GetInt64(0), r.GetInt64(1), r.GetInt32(2), r.GetString(3), r.GetString(4)),
            $"task {taskId} not found", ct);

    public Task SetTaskStatusAsync(long taskId, string status, IOrchestrationUnitOfWork uow, CancellationToken ct) =>
        ExecAsync(uow, "UPDATE orch_task SET status = @status WHERE id = @id",
            p =>
            {
                p.AddWithValue("status", status);
                p.AddWithValue("id", taskId);
            }, ct);

    public async Task<int> CountTasksAsync(long workflowId, string type, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        var connection = ((PgUnitOfWork)uow).Connection;
        await using var cmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM orch_task WHERE workflow_id = @workflow AND type = @type",
            connection, ((PgUnitOfWork)uow).Transaction);
        cmd.Parameters.AddWithValue("workflow", workflowId);
        cmd.Parameters.AddWithValue("type", type);
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<long> CreateRunAsync(long taskId, RunInput input, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        var connection = ((PgUnitOfWork)uow).Connection;
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO orch_run (task_id, status, input)
            VALUES (@task, 'scheduled', @input::jsonb)
            RETURNING id
            """, connection, ((PgUnitOfWork)uow).Transaction);
        cmd.Parameters.AddWithValue("task", taskId);
        cmd.Parameters.AddWithValue("input", JsonSerializer.Serialize(input, JsonOptions));
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public Task<RunRow> GetRunAsync(long runId, IOrchestrationUnitOfWork uow, CancellationToken ct) =>
        QueryRequiredAsync(uow, "SELECT id, task_id, status, input FROM orch_run WHERE id = @id",
            p => p.AddWithValue("id", runId),
            r => new RunRow(r.GetInt64(0), r.GetInt64(1), r.GetString(2),
                JsonSerializer.Deserialize<RunInput>(r.GetString(3), JsonOptions)!),
            $"run {runId} not found", ct);

    public Task SetRunStatusAsync(long runId, string status, IOrchestrationUnitOfWork uow, CancellationToken ct) =>
        ExecAsync(uow, "UPDATE orch_run SET status = @status WHERE id = @id",
            p =>
            {
                p.AddWithValue("status", status);
                p.AddWithValue("id", runId);
            }, ct);

    public async Task<IReadOnlyList<RunRow>> ListRunsAsync(long taskId, string status, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        var connection = ((PgUnitOfWork)uow).Connection;
        await using var cmd = new NpgsqlCommand(
            "SELECT id, task_id, status, input FROM orch_run WHERE task_id = @task AND status = @status ORDER BY id",
            connection, ((PgUnitOfWork)uow).Transaction);
        cmd.Parameters.AddWithValue("task", taskId);
        cmd.Parameters.AddWithValue("status", status);

        var runs = new List<RunRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            runs.Add(new RunRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                JsonSerializer.Deserialize<RunInput>(reader.GetString(3), JsonOptions)!));
        return runs;
    }
}
