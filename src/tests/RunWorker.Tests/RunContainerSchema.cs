using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiAgent.Events.Delivery;
using PiAgent.RunWorker;
using PiAgent.Tests.Postgres;

namespace PiAgent.RunWorker.Tests;

/// <summary>
/// Schema helper for the worker's durable registry in tests: a minimal `orch_run`
/// (FK target) and the `run_container` table from the real store DDL. One container
/// per test assembly, so these tables live only in the RunWorker.Tests database.
/// </summary>
internal static class RunContainerSchema
{
    public static RunRegistryStore Store(PostgresFixture fx) => new(
        Options.Create(new EventLogOptions { ConnectionString = fx.ConnectionString }),
        NullLogger<RunRegistryStore>.Instance);

    public static async Task EnsureAsync(PostgresFixture fx, CancellationToken ct = default)
    {
        // minimal FK target — the real DDL is the orchestration module's business
        await Exec(fx, """
            CREATE TABLE IF NOT EXISTS orch_run (
                id         bigserial PRIMARY KEY,
                task_id    bigint NOT NULL,
                status     text   NOT NULL,
                input      jsonb  NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now()
            )
            """, ct);
        // the real DDL, from the store itself — no schema duplication in tests
        await Store(fx).EnsureSchemaAsync(ct);
    }

    /// <summary>Clean slate between tests: registry rows and their parent runs.</summary>
    public static async Task ResetAsync(PostgresFixture fx, CancellationToken ct = default)
    {
        // one statement: Postgres blocks truncating the FK-referenced orch_run alone
        await Exec(fx, "TRUNCATE run_container, orch_run RESTART IDENTITY", ct);
    }

    /// <summary>The FK parent every provisioned run needs. `input` is the RunInput JSON
    /// the Orchestrator would have written — the registry reads it back on recovery.</summary>
    public static Task SeedRunAsync(PostgresFixture fx, long runId, long taskId, string input = "{}", CancellationToken ct = default)
    {
        using var cmd = fx.DataSource.CreateCommand(
            "INSERT INTO orch_run (id, task_id, status, input) VALUES (@id, @task, 'scheduled', @input::jsonb) ON CONFLICT DO NOTHING");
        cmd.Parameters.AddWithValue("id", runId);
        cmd.Parameters.AddWithValue("task", taskId);
        cmd.Parameters.AddWithValue("input", input);
        return cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task Exec(PostgresFixture fx, string sql, CancellationToken ct)
    {
        await using var cmd = fx.DataSource.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
