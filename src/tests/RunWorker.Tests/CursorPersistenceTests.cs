using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using PiAgent.Events;
using PiAgent.Events.Delivery;
using PiAgent.Events.Postgres;
using PiAgent.GithubSync;
using Testcontainers.PostgreSql;
using Xunit;

namespace PiAgent.RunWorker.Tests;

/// <summary>
/// The sync's cursor discipline against real Postgres: a cursor advance joins the EventLog
/// transaction (docs/github-sync.md) — events and the cursor commit atomically, and a
/// content-fingerprinted record re-detected in the overlap window yields one row.
/// </summary>
public sealed class CursorPersistenceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();
    private EventQueueStore _store = null!;
    private PostgresCursorStore _cursors = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        var connectionString = _container.GetConnectionString();
        _store = new EventQueueStore(
            Options.Create(new EventLogOptions { ConnectionString = connectionString }),
            NullLogger<EventQueueStore>.Instance);
        await _store.EnsureSchemaAsync(CancellationToken.None);
        _cursors = new PostgresCursorStore(
            NpgsqlDataSource.Create(connectionString), NullLogger<PostgresCursorStore>.Instance);
        await _cursors.EnsureSchemaAsync(CancellationToken.None);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private static ItemChanged ItemChanged(string eventId) => new(
        "o", "r", 25, IsPullRequest: false, "t", "b", "open", EventId: eventId);

    [Fact]
    public async Task Cursor_advance_commits_atomically_with_the_events()
    {
        var watermark = DateTime.UtcNow.Ticks.ToString();

        await using var uow = await _store.BeginTransactionAsync(CancellationToken.None);
        await _log_Append(uow, ItemChanged("item:25:100"));
        await _cursors.SetAsync("o/r", "items", watermark, uow, CancellationToken.None);
        await uow.CommitAsync(CancellationToken.None);

        Assert.Equal(watermark, await _cursors.GetAsync("o/r", "items", CancellationToken.None));
    }

    [Fact]
    public async Task A_refetched_fingerprint_yields_one_row()
    {
        var first = await _store.AppendAsync(ItemChanged("item:25:100"), CancellationToken.None);
        var second = await _store.AppendAsync(ItemChanged("item:25:100"), CancellationToken.None);

        Assert.Equal(first, second);
    }

    private async Task<long> _log_Append(IEventTransaction uow, IEvent evt) =>
        await ((IEventLog)_store).AppendAsync(evt, uow, CancellationToken.None);
}
