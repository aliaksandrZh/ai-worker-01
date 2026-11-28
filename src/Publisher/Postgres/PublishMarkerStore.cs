using Microsoft.Extensions.Logging;
using Npgsql;
using PiAgent.Events;
using PiAgent.Events.Postgres;

namespace PiAgent.Publisher.Postgres;

public sealed class PostgresPublishMarkerStore(NpgsqlDataSource dataSource, ILogger<PostgresPublishMarkerStore> logger)
    : IPublishMarkerStore
{
    public async Task<bool> ExistsAsync(string idempotencyKey, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("SELECT EXISTS(SELECT 1 FROM publish_marker WHERE key = @key)");
        cmd.Parameters.AddWithValue("key", idempotencyKey);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task InsertAsync(string idempotencyKey, IEventTransaction? tx, CancellationToken ct)
    {
        var (connection, transaction) = EventQueueStore.Unwrap(tx ?? NullEventTransaction.Instance);
        await using var cmd = transaction is null
            ? dataSource.CreateCommand(
                "INSERT INTO publish_marker (key, published_at) VALUES (@key, now()) ON CONFLICT DO NOTHING")
            : new NpgsqlCommand(
                "INSERT INTO publish_marker (key, published_at) VALUES (@key, now()) ON CONFLICT DO NOTHING",
                connection, transaction);
        cmd.Parameters.AddWithValue("key", idempotencyKey);
        await cmd.ExecuteNonQueryAsync(ct);
        logger.LogDebug("publish marker {key} written", idempotencyKey);
    }

    /// <summary>Idempotent DDL for the marker table.</summary>
    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS publish_marker (
                key          text PRIMARY KEY,
                published_at timestamptz NOT NULL DEFAULT now()
            );
            """;
        await using var cmd = dataSource.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private sealed class NullEventTransaction : IEventTransaction
    {
        public static readonly NullEventTransaction Instance = new();

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public long? CausationId => null;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
