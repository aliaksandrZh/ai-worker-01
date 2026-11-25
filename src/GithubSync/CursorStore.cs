using Microsoft.Extensions.Logging;
using Npgsql;
using PiAgent.Events;
using PiAgent.Events.Postgres;

namespace PiAgent.GithubSync;

/// <summary>
/// The sync adapter's per-repo, per-stream cursors (docs/github-sync.md invariant 1) —
/// the only sync state that exists; there is no content mirror. Cursor advances join the
/// EventLog transaction so detection never re-emits a processed change.
/// </summary>
public interface ICursorStore
{
    Task<string?> GetAsync(string repo, string stream, CancellationToken ct);

    Task SetAsync(string repo, string stream, string cursor, IEventTransaction? tx, CancellationToken ct);
}

public sealed class PostgresCursorStore(NpgsqlDataSource dataSource, ILogger<PostgresCursorStore> logger) : ICursorStore
{
    public async Task<string?> GetAsync(string repo, string stream, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT cursor FROM sync_cursor WHERE repo = @repo AND stream = @stream");
        cmd.Parameters.AddWithValue("repo", repo);
        cmd.Parameters.AddWithValue("stream", stream);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task SetAsync(string repo, string stream, string cursor, IEventTransaction? tx, CancellationToken ct)
    {
        var (connection, transaction) = EventQueueStore.Unwrap(tx ?? NullTransaction.Instance);
        await using var cmd = transaction is null
            ? dataSource.CreateCommand(
                "INSERT INTO sync_cursor (repo, stream, cursor) VALUES (@repo, @stream, @cursor) ON CONFLICT (repo, stream) DO UPDATE SET cursor = EXCLUDED.cursor")
            : new NpgsqlCommand(
                "INSERT INTO sync_cursor (repo, stream, cursor) VALUES (@repo, @stream, @cursor) ON CONFLICT (repo, stream) DO UPDATE SET cursor = EXCLUDED.cursor",
                connection, transaction);
        cmd.Parameters.AddWithValue("repo", repo);
        cmd.Parameters.AddWithValue("stream", stream);
        cmd.Parameters.AddWithValue("cursor", cursor);
        await cmd.ExecuteNonQueryAsync(ct);
        logger.LogDebug("cursor {repo}/{stream} → {cursor}", repo, stream, cursor);
    }

    /// <summary>Idempotent DDL for the cursor table.</summary>
    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS sync_cursor (
                repo   text NOT NULL,
                stream text NOT NULL,
                cursor text NOT NULL,
                PRIMARY KEY (repo, stream)
            );
            """;
        await using var cmd = dataSource.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private sealed class NullTransaction : IEventTransaction
    {
        public static readonly NullTransaction Instance = new();

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class InMemoryCursorStore : ICursorStore
{
    public Dictionary<string, string> Cursors { get; } = [];

    public Task<string?> GetAsync(string repo, string stream, CancellationToken ct) =>
        Task.FromResult(Cursors.TryGetValue($"{repo}/{stream}", out var value) ? value : null);

    public Task SetAsync(string repo, string stream, string cursor, IEventTransaction? tx, CancellationToken ct)
    {
        Cursors[$"{repo}/{stream}"] = cursor;
        return Task.CompletedTask;
    }
}
