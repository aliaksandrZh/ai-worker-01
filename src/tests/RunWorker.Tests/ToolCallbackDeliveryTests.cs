using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiAgent.Events;
using PiAgent.Events.Delivery;
using PiAgent.Events.Postgres;
using PiAgent.RunWorker;
using Testcontainers.PostgreSql;
using Xunit;

namespace PiAgent.RunWorker.Tests;

/// <summary>
/// The route tool delivery contract against real Postgres (docker required): ingestion
/// dedup on the deterministic eventId, and the transient-failure path — repeated handler
/// exceptions ride the EventLog's redelivery into the dead-letter store, payload intact.
/// </summary>
public sealed class ToolCallbackDeliveryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();
    private EventQueueStore _store = null!;
    private string _connectionString = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _connectionString = _container.GetConnectionString();
        _store = new EventQueueStore(
            Options.Create(new EventLogOptions { ConnectionString = _connectionString }),
            NullLogger<EventQueueStore>.Instance);
        await _store.EnsureSchemaAsync(CancellationToken.None);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private static AgentCallback ToolCallback(string eventId) => new(
        RunId: 7, Route: "tool",
        Body: "{\"eventId\":\"" + eventId + "\",\"occurredAt\":\"2026-01-01T00:00:00Z\",\"toolName\":\"save_plan\",\"args\":{\"goals\":\"g\",\"scope\":\"s\",\"acceptanceCriteria\":\"a\",\"steps\":[]}}",
        EventId: eventId);

    [Fact]
    public async Task Redelivery_of_the_same_eventId_yields_one_row()
    {
        var first = await _store.AppendAsync(ToolCallback("7-tool-1"), CancellationToken.None);
        var second = await _store.AppendAsync(ToolCallback("7-tool-1"), CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(1, await CountRowsAsync("7-tool-1"));

        // a different eventId is a different call — its own row
        await _store.AppendAsync(ToolCallback("7-tool-2"), CancellationToken.None);
        Assert.Equal(1, await CountRowsAsync("7-tool-2"));
    }

    [Fact]
    public async Task Callbacks_without_an_eventId_do_not_collide()
    {
        await _store.AppendAsync(new AgentCallback(7, "progress", Body: null), CancellationToken.None);
        await _store.AppendAsync(new AgentCallback(7, "progress", Body: null), CancellationToken.None);

        Assert.Equal(2, await CountRowsAsync(null));
    }

    [Fact]
    public async Task Transient_handler_failures_dead_letter_the_callback_with_its_payload()
    {
        var id = await _store.AppendAsync(ToolCallback("7-tool-1"), CancellationToken.None);

        var registry = new RunRegistry();
        registry.Add(new RunEntry(7, TaskId: 3, WorkflowId: 2,
            Input: new RunInput("o", "r", 1, PrNumber: 1, Command: "plan", PlanCommentId: 0,
                Profile: "planner", AgentBranch: null, DefaultBranch: "main", ReviewHeadSha: null),
            ContainerName: "pi-session-7", Token: "t", StartedUtc: DateTime.UtcNow));

        var consumer = new RunWorkerConsumer(
            registry, new RecordingProvisioner(),
            new ThrowingAppendLog(_store),
            Options.Create(new RunWorkerOptions { WorkspaceRoot = Path.GetTempPath() }),
            NullLogger<RunWorkerConsumer>.Instance);

        var callbackTypes = new[] { "AgentCallback" };
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var claimed = await _store.ClaimAsync(callbackTypes, 10, TimeSpan.FromSeconds(1), CancellationToken.None);
            var evt = Assert.Single(claimed);
            Assert.Equal(id, evt.Id);

            // the handler throws (transient outage appending the typed event) → no ack
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => consumer.HandleAsync(evt.Event, evt.Attempt, CancellationToken.None));
            await _store.FailAsync(evt.Id, "transient log outage", maxAttempts: 3, CancellationToken.None);
        }

        var dead = await _store.ListDeadLettersAsync(10, CancellationToken.None);
        var letter = Assert.Single(dead);
        Assert.Equal("AgentCallback", letter.Type);
        Assert.Contains("7-tool-1", (await DeadLetterPayloadAsync(letter.Id))!);
    }

    private async Task<int> CountRowsAsync(string? eventId)
    {
        await using var conn = Npgsql.NpgsqlDataSource.Create(_connectionString);
        await using var cmd = conn.CreateCommand();
        if (eventId is null)
        {
            cmd.CommandText = "SELECT COUNT(*) FROM events WHERE type = 'AgentCallback' AND payload->>'eventId' IS NULL";
        }
        else
        {
            cmd.CommandText = "SELECT COUNT(*) FROM events WHERE type = 'AgentCallback' AND payload->>'eventId' = @id";
            cmd.Parameters.AddWithValue("id", eventId);
        }
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<string?> DeadLetterPayloadAsync(long id)
    {
        await using var conn = Npgsql.NpgsqlDataSource.Create(_connectionString);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT payload::text FROM dead_letter WHERE id = @id";
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteScalarAsync() as string;
    }

    /// <summary>The producer side that fails: appends throw, the log stays up otherwise.</summary>
    private sealed class ThrowingAppendLog(IEventLog inner) : IEventLog
    {
        public Task<long> AppendAsync<TEvent>(TEvent evt, CancellationToken ct) where TEvent : IEvent =>
            throw new InvalidOperationException("transient log outage");

        public Task<long> AppendAsync<TEvent>(TEvent evt, IEventTransaction tx, CancellationToken ct)
            where TEvent : IEvent =>
            throw new InvalidOperationException("transient log outage");

        public Task<IEventTransaction> BeginTransactionAsync(CancellationToken ct) =>
            inner.BeginTransactionAsync(ct);
    }
}
