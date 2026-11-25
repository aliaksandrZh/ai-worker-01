using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiAgent.Events.Delivery;
using PiAgent.Events.Postgres;
using Testcontainers.PostgreSql;
using Xunit;

namespace PiAgent.Events.Tests;

/// <summary>
/// The Postgres adapter against real postgres: schema bootstrap, claim/redelivery,
/// dead-letter, replay (docker required).
/// </summary>
public sealed class PostgresEventQueueTests : IAsyncLifetime
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

    private static readonly string[] CommentType = ["CommentChanged"];

    [Fact]
    public async Task Appended_event_is_claimed_and_acked()
    {
        var id = await _store.AppendAsync(
            new CommentChanged("o", "r", 1, 10, "a", "b", Deleted: false), CancellationToken.None);

        var claimed = await _store.ClaimAsync(CommentType, 10, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.Single(claimed);
        Assert.Equal(id, claimed[0].Id);
        Assert.Equal(10, Assert.IsType<CommentChanged>(claimed[0].Event).CommentId);

        // claimed_until holds it back until the visibility window passes
        var again = await _store.ClaimAsync(CommentType, 10, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.Empty(again);

        await _store.AckAsync(id, CancellationToken.None);
        var afterAck = await _store.ClaimAsync(CommentType, 10, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.Empty(afterAck);
    }

    [Fact]
    public async Task Failed_event_redelivers_then_dead_letters_after_max_attempts()
    {
        var id = await _store.AppendAsync(
            new CommentChanged("o", "r", 1, 10, "a", "b", Deleted: false), CancellationToken.None);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await _store.ClaimAsync(CommentType, 10, TimeSpan.FromSeconds(1), CancellationToken.None);
            await _store.FailAsync(id, "boom", 3, CancellationToken.None);
        }

        var dead = await _store.ListDeadLettersAsync(10, CancellationToken.None);
        Assert.Single(dead);
        Assert.Equal("CommentChanged", dead[0].Type);

        var replayed = await _store.ReplayDeadLetterAsync(dead[0].Id, CancellationToken.None);
        Assert.True(replayed);
        var claimed = await _store.ClaimAsync(CommentType, 10, TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.Single(claimed);
        Assert.Equal(1, claimed[0].Attempt); // replay resets the counter
    }
}
