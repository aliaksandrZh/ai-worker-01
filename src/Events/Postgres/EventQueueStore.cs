using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using PiAgent.Events.Delivery;

namespace PiAgent.Events.Postgres;

/// <summary>
/// The Postgres event log: one store implementing the producer side (IEventLog) and the
/// delivery side (IEventQueue). Idempotent DDL at startup — the formal baseline migration
/// lands with the stage-3 schema reset (docs/adr/0004).
/// </summary>
public sealed class EventQueueStore : IEventLog, IEventQueue
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<EventQueueStore> _logger;

    public EventQueueStore(IOptions<EventLogOptions> options, ILogger<EventQueueStore> logger)
    {
        Console.Error.Flush();
        _dataSource = NpgsqlDataSource.Create(options.Value.ConnectionString);
        _logger = logger;
    }

    /// <summary>Idempotent bootstrap DDL; safe to call on every host start.</summary>
    public async Task EnsureSchemaAsync(CancellationToken ct)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS events (
                id            bigserial PRIMARY KEY,
                stream        text        NOT NULL,
                type          text        NOT NULL,
                payload       jsonb       NOT NULL,
                not_before    timestamptz,
                created_at    timestamptz NOT NULL DEFAULT now(),
                processed_at  timestamptz,
                claimed_until timestamptz,
                attempts      int         NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_events_claim
                ON events (stream, id) WHERE processed_at IS NULL;
            -- callback dedup (docs/run-worker.md): only rows carrying the Bridge's
            -- deterministic eventId participate; other types keep NULL keys (never equal).
            CREATE UNIQUE INDEX IF NOT EXISTS ux_events_type_event_id
                ON events (type, (payload->>'eventId'));
            CREATE TABLE IF NOT EXISTS dead_letter (
                id          bigserial PRIMARY KEY,
                stream      text   NOT NULL,
                type        text   NOT NULL,
                payload     jsonb  NOT NULL,
                attempts    int    NOT NULL,
                last_error  text,
                dead_at     timestamptz NOT NULL DEFAULT now()
            );
            """;
        await using var cmd = _dataSource.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("event log schema ensured");
    }

    // ---- producer side ---------------------------------------------------------

    public async Task<long> AppendAsync<TEvent>(TEvent evt, CancellationToken ct) where TEvent : IEvent
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var id = await AppendCoreAsync(conn, tx, evt, ct);
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<long> AppendAsync<TEvent>(TEvent evt, IEventTransaction tx, CancellationToken ct)
        where TEvent : IEvent
    {
        var txImpl = (EventTransaction)tx;
        return await AppendCoreAsync(txImpl.Connection, txImpl.Transaction, evt, ct);
    }

    public async Task<IEventTransaction> BeginTransactionAsync(CancellationToken ct)
    {
        var conn = await _dataSource.OpenConnectionAsync(ct);
        var tx = await conn.BeginTransactionAsync(ct);
        return new EventTransaction(conn, tx);
    }

    /// <summary>
    /// Wraps a caller-owned Npgsql transaction so event appends commit atomically with
    /// the caller's state change (docs/eventlog.md invariant 6) — the Orchestrator's
    /// unit-of-work uses this to share one transaction across stores.
    /// </summary>
    public IEventTransaction WrapTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction) =>
        new EventTransaction(connection, transaction);

    /// <summary>Underlying Npgsql transaction of a store-created IEventTransaction (or null for foreign ones).</summary>
    public static (NpgsqlConnection? Connection, NpgsqlTransaction? Transaction) Unwrap(IEventTransaction tx) =>
        tx is EventTransaction own ? (own.Connection, own.Transaction) : (null, null);

    private static async Task<long> AppendCoreAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, IEvent evt, CancellationToken ct)
    {
        // Callback dedup (docs/run-worker.md): spool redelivery repeats the deterministic
        // eventId verbatim — ON CONFLICT DO NOTHING keeps the row unique and the existing
        // row's id is returned, so no consumer sees a duplicate.
        var eventId = (evt as AgentCallback)?.EventId;
        const string sql = """
            WITH inserted AS (
                INSERT INTO events (stream, type, payload)
                VALUES (@stream, @type, @payload::jsonb)
                ON CONFLICT (type, (payload->>'eventId')) DO NOTHING
                RETURNING id
            ), redelivered AS (
                SELECT id FROM events
                WHERE type = @type AND @event_id::text IS NOT NULL
                                   AND payload->>'eventId' = @event_id
            )
            SELECT id FROM inserted
            UNION ALL
            SELECT id FROM redelivered
            LIMIT 1
            """;
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("stream", evt.StreamKey);
        cmd.Parameters.AddWithValue("type", EventRegistry.TypeName(evt.GetType()));
        cmd.Parameters.AddWithValue("payload", EventRegistry.Serialize(evt));
        cmd.Parameters.AddWithValue("event_id", (object?)eventId ?? DBNull.Value);
        var id = await cmd.ExecuteScalarAsync(ct);
        return id is long value ? value : throw new InvalidOperationException(
            $"event append produced no row (dedup conflict with a different payload): {evt.GetType().Name}");
    }

    private sealed class EventTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction)
        : IEventTransaction
    {
        public NpgsqlConnection Connection { get; } = connection;
        public NpgsqlTransaction Transaction { get; } = transaction;

        public async Task CommitAsync(CancellationToken ct)
        {
            await Transaction.CommitAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            await Transaction.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    // ---- delivery side ---------------------------------------------------------

    public async Task<IReadOnlyList<ClaimedEvent>> ClaimAsync(
        IReadOnlyCollection<string> types, int batch, TimeSpan visibility, CancellationToken ct)
    {
        // Head-of-line per stream: never claim an event while an older unprocessed event
        // of the same stream exists; skip rows other instances already claimed.
        const string sql = """
            UPDATE events e
            SET claimed_until = now() + make_interval(secs => @visibility), attempts = e.attempts + 1
            WHERE e.id IN (
                SELECT e2.id FROM events e2
                WHERE e2.type = ANY(@types)
                  AND e2.processed_at IS NULL
                  AND (e2.claimed_until IS NULL OR e2.claimed_until <= now())
                  AND (e2.not_before IS NULL OR e2.not_before <= now())
                  AND NOT EXISTS (
                      SELECT 1 FROM events e3
                      WHERE e3.stream = e2.stream AND e3.id < e2.id
                        AND e3.type = ANY(@types) AND e3.processed_at IS NULL)
                LIMIT @batch
            )
            RETURNING e.id, e.stream, e.type, e.payload, e.attempts
            """;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("types", types.ToArray());
        cmd.Parameters.AddWithValue("visibility", (int)visibility.TotalSeconds);
        cmd.Parameters.AddWithValue("batch", batch);

        var claimed = new List<ClaimedEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var payload = reader.GetString(3);
            var evt = EventRegistry.Deserialize(reader.GetString(2), payload);
            claimed.Add(new ClaimedEvent(reader.GetInt64(0), evt, reader.GetInt32(4)));
        }

        return claimed;
    }

    public async Task AckAsync(long id, CancellationToken ct)
    {
        const string sql = "UPDATE events SET processed_at = now(), claimed_until = NULL WHERE id = @id";
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task FailAsync(long id, string error, int maxAttempts, CancellationToken ct)
    {
        // exactly one of the two statements matches (attempts already counted at claim time):
        // over the budget → the event moves to dead-letter, under it → released for redelivery.
        const string sql = """
            WITH moved AS (
                DELETE FROM events
                WHERE id = @id AND attempts >= @maxAttempts
                RETURNING stream, type, payload, attempts
            ), released AS (
                UPDATE events SET claimed_until = NULL
                WHERE id = @id AND attempts < @maxAttempts
            )
            INSERT INTO dead_letter (stream, type, payload, attempts, last_error)
            SELECT stream, type, payload, attempts, @error
            FROM moved
            RETURNING id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("error", error);
        cmd.Parameters.AddWithValue("maxAttempts", maxAttempts);
        var parked = await cmd.ExecuteScalarAsync(ct);

        if (parked is not null)
            _logger.LogError("event {id} moved to dead-letter after repeated failures", id);
    }

    public async Task PoisonAsync(long id, string error, CancellationToken ct)
    {
        const string sql = """
            WITH moved AS (
                DELETE FROM events
                WHERE id = @id
                RETURNING stream, type, payload, attempts
            )
            INSERT INTO dead_letter (stream, type, payload, attempts, last_error)
            SELECT stream, type, payload, attempts, @error
            FROM moved
            RETURNING id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("error", error);
        var parked = await cmd.ExecuteScalarAsync(ct);

        if (parked is not null)
            _logger.LogError("event {id} poisoned straight to dead-letter: {error}", id, error);
    }

    public async Task<IReadOnlyList<DeadLetterEntry>> ListDeadLettersAsync(int limit, CancellationToken ct)
    {
        const string sql = """
            SELECT id, type, stream, attempts, last_error, dead_at
            FROM dead_letter ORDER BY id DESC LIMIT @limit
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("limit", limit);

        var entries = new List<DeadLetterEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            entries.Add(new DeadLetterEntry(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetDateTime(5)));
        }

        return entries;
    }

    public async Task<bool> ReplayDeadLetterAsync(long deadLetterId, CancellationToken ct)
    {
        const string sql = """
            WITH parked AS (
                DELETE FROM dead_letter WHERE id = @id
                RETURNING stream, type, payload
            )
            INSERT INTO events (stream, type, payload)
            SELECT stream, type, payload FROM parked
            RETURNING id
            """;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", deadLetterId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }
}
