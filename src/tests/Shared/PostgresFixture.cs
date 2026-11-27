using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using PiAgent.Events;
using PiAgent.Events.Delivery;
using PiAgent.Events.Postgres;
using Testcontainers.PostgreSql;
using Xunit;

namespace PiAgent.Tests.Postgres;

/// <summary>
/// The EventLog contract against the real Postgres adapter — there is no second
/// (in-memory) implementation to keep in parity. One container per test assembly
/// (shared through the collection fixture); schemas bootstrap once, tables truncate
/// between tests. Compiled into every test assembly that drives the log.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();

    public EventQueueStore Log { get; private set; } = null!;
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    /// <summary>The container's real connection string — NpgsqlDataSource.ConnectionString
    /// masks the password, so stores building their own data source take this one.</summary>
    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        DataSource = NpgsqlDataSource.Create(ConnectionString);
        Log = new EventQueueStore(
            Options.Create(new EventLogOptions { ConnectionString = _container.GetConnectionString() }),
            NullLogger<EventQueueStore>.Instance);
        await Log.EnsureSchemaAsync(CancellationToken.None);
    }

    /// <summary>Clean slate for the next test: the log and, when its schema exists, the
    /// publisher markers. Orchestration tables are the orchestration tests' own business
    /// (their schema, their reset).</summary>
    public async Task ResetCoreAsync(CancellationToken ct = default)
    {
        await using var cmd = DataSource.CreateCommand("TRUNCATE events, dead_letter RESTART IDENTITY");
        await cmd.ExecuteNonQueryAsync(ct);

        await using var probe = DataSource.CreateCommand("SELECT to_regclass('publish_marker')::text");
        if (await probe.ExecuteScalarAsync(ct) is string { Length: > 0 })
        {
            await using var marker = DataSource.CreateCommand("TRUNCATE publish_marker RESTART IDENTITY");
            await marker.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Every appended event, oldest first — the assertion view of the log.</summary>
    public async Task<IReadOnlyList<IEvent>> AppendedAsync(CancellationToken ct = default)
    {
        var appended = new List<IEvent>();
        await using var cmd = DataSource.CreateCommand("SELECT type, payload FROM events ORDER BY id");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            appended.Add(EventRegistry.Deserialize(reader.GetString(0), reader.GetString(1)));
        return appended;
    }

    /// <summary>Unprocessed events — the pending queue, as a consumer would see it.</summary>
    public async Task<int> PendingCountAsync(CancellationToken ct = default)
    {
        await using var cmd = DataSource.CreateCommand(
            "SELECT count(*) FROM events WHERE processed_at IS NULL");
        return (int)(long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    /// <summary>Release every claim now, so the next claim round sees them (the in-memory
    /// store's ExpireClaims, as SQL against the visibility column).</summary>
    public async Task ExpireClaimsAsync(CancellationToken ct = default)
    {
        await using var cmd = DataSource.CreateCommand(
            "UPDATE events SET claimed_until = NULL WHERE claimed_until IS NOT NULL");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition("PostgresLog")]
public sealed class PostgresLogCollection : ICollectionFixture<PostgresFixture>;
