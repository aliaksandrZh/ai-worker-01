using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiAgent.Events;
using PiAgent.Events.Delivery;
using PiAgent.Events.Postgres;
using Testcontainers.PostgreSql;
using Xunit;

namespace PiAgent.RunWorker.Tests;

/// <summary>
/// The EventLog's deterministic-eventId dedup against real Postgres: a content-fingerprinted
/// record re-detected by the sync yields one row — the fingerprint is the sync's only memory
/// (docs/github-sync.md invariant 1).
/// </summary>
public sealed class EventLogFingerprintTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();
    private EventQueueStore _store = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _store = new EventQueueStore(
            Options.Create(new EventLogOptions { ConnectionString = _container.GetConnectionString() }),
            NullLogger<EventQueueStore>.Instance);
        await _store.EnsureSchemaAsync(CancellationToken.None);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private static ItemChanged ItemChanged(string eventId) => new(
        "o", "r", 25, IsPullRequest: false, "t", "b", "open", [], EventId: eventId);

    [Fact]
    public async Task A_refetched_fingerprint_yields_one_row()
    {
        var first = await _store.AppendAsync(ItemChanged("item:25:100"), CancellationToken.None);
        var second = await _store.AppendAsync(ItemChanged("item:25:100"), CancellationToken.None);

        Assert.Equal(first, second);
    }
}
