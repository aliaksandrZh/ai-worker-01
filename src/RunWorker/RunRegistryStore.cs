using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using PiAgent.Events;
using PiAgent.Events.Delivery;

namespace PiAgent.RunWorker;

/// <summary>
/// The run registry (docs/run-worker.md invariant 3), persisted in `run_container`: one
/// row per provisioned Run — the callback token, the container handle and the run's
/// lifetime. There is no in-memory copy: the callback surface, the consumer and the
/// watchdog all read here; rows with a null finished_at are the in-flight set.
/// </summary>
public sealed class RunRegistryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<RunRegistryStore> _logger;

    public RunRegistryStore(IOptions<EventLogOptions> options, ILogger<RunRegistryStore> logger)
    {
        _dataSource = NpgsqlDataSource.Create(options.Value.ConnectionString);
        _logger = logger;
    }

    /// <summary>Idempotent bootstrap DDL. The FK target `orch_run` is the Orchestrator
    /// module's table — its store bootstraps first (SchemaBootstrap order in the app host).
    /// A row is never deleted: finished_at marks the run settled, null keeps it in flight.</summary>
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS run_container (
                run_id         bigint PRIMARY KEY REFERENCES orch_run(id),
                container_id   text   NOT NULL,
                callback_token text   NOT NULL,
                workflow_id    bigint NOT NULL,
                started_at     timestamptz NOT NULL,
                finished_at    timestamptz
            );
            """;
        await using var cmd = _dataSource.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("run container registry schema ensured");
    }

    /// <summary>The provisioning fact: the run's container exists and carries the token.
    /// Called once per provisioned Run, inside the provisioning flow — a Run without its
    /// durable row cannot be recovered after a restart, so insert failures fail provisioning.</summary>
    public async Task InsertAsync(RunEntry entry, string containerId, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO run_container (run_id, container_id, callback_token, workflow_id, started_at)
            VALUES (@run, @container, @token, @workflow, @started)
            ON CONFLICT (run_id) DO NOTHING
            """;
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("run", entry.RunId);
        cmd.Parameters.AddWithValue("container", containerId);
        cmd.Parameters.AddWithValue("token", entry.Token);
        cmd.Parameters.AddWithValue("workflow", entry.WorkflowId);
        cmd.Parameters.AddWithValue("started", entry.StartedUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The Bridge egress auth fallback: when the in-memory cache misses (worker
    /// restart), the durable row re-admits the callback and re-warms the cache.</summary>
    public async Task<RunEntry?> GetByTokenAsync(string token, CancellationToken ct = default)
    {
        const string sql = """
            SELECT rc.run_id, o.task_id, rc.workflow_id, o.input, rc.container_id, rc.callback_token, rc.started_at
            FROM run_container rc
            JOIN orch_run o ON o.id = rc.run_id
            WHERE rc.callback_token = @token AND rc.finished_at IS NULL
            """;
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("token", token);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapEntry(reader) : null;
    }

    /// <summary>The in-flight rows — the recovery source for the worker's registry cache.</summary>
    public async Task<IReadOnlyList<RunEntry>> LoadUnfinishedAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT rc.run_id, o.task_id, rc.workflow_id, o.input, rc.container_id, rc.callback_token, rc.started_at
            FROM run_container rc
            JOIN orch_run o ON o.id = rc.run_id
            WHERE rc.finished_at IS NULL
            ORDER BY rc.run_id
            """;
        var entries = new List<RunEntry>();
        await using var cmd = _dataSource.CreateCommand(sql);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            entries.Add(MapEntry(reader));
        return entries;
    }

    /// <summary>The in-flight run by id — the callback/stop path's entry lookup.
    /// Null when the run is settled or was never provisioned.</summary>
    public async Task<RunEntry?> GetAsync(long runId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT rc.run_id, o.task_id, rc.workflow_id, o.input, rc.container_id, rc.callback_token, rc.started_at
            FROM run_container rc
            JOIN orch_run o ON o.id = rc.run_id
            WHERE rc.run_id = @run AND rc.finished_at IS NULL
            """;
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("run", runId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapEntry(reader) : null;
    }

    /// <summary>Idempotent settle fact; rows are kept for diagnosability, never deleted.</summary>
    public async Task MarkFinishedAsync(long runId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE run_container SET finished_at = now()
            WHERE run_id = @run AND finished_at IS NULL
            """;
        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("run", runId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static RunEntry MapEntry(NpgsqlDataReader r)
    {
        var input = JsonSerializer.Deserialize<RunInput>(r.GetString(3), JsonOptions)!;
        return new RunEntry(
            r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), input,
            // the container handle: the stored docker id addresses inspect/stop robustly
            // (the derived name would also work, the id is the stronger fact we hold)
            ContainerName: r.GetString(4),
            Token: r.GetString(5),
            StartedUtc: r.GetDateTime(6));
    }
}
