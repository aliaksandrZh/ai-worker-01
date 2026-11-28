using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiAgent.Events;
using PiAgent.Events.Delivery;
using PiAgent.Events.Postgres;
using PiAgent.Tests.Postgres;
using Xunit;

namespace PiAgent.RunWorker.Tests;

/// <summary>
/// The EventLog's deterministic-eventId dedup against the real Postgres adapter: a
/// content-fingerprinted record re-detected by the sync yields one row — the fingerprint is
/// the sync's only memory (docs/github-sync.md invariant 1).
/// </summary>
[Collection("PostgresLog")]
public sealed class EventLogFingerprintTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private readonly EventQueueStore _store;

    public EventLogFingerprintTests(PostgresFixture fx)
    {
        _fx = fx;
        _store = fx.Log;
    }

    public Task InitializeAsync() => _fx.ResetCoreAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static ItemChanged ItemChanged(string eventId) => new ItemChanged
    {
        Owner = "o", Repo = "r", Number = 25, IsPullRequest = false,
        Title = "t", Body = "b", State = "open", Labels = [], EventId = eventId,
    };

    [Fact]
    public async Task A_refetched_fingerprint_yields_one_row()
    {
        var first = await _store.AppendAsync(ItemChanged("item:25:100"), CancellationToken.None);
        var second = await _store.AppendAsync(ItemChanged("item:25:100"), CancellationToken.None);

        Assert.Equal(first, second);
    }
}
