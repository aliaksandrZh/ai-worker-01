using PiAgent.Events;
using PiAgent.Events.Postgres;
using PiAgent.Tests.Postgres;
using Xunit;

namespace PiAgent.Events.Tests;

/// <summary>
/// The Postgres adapter's claim mechanics directly: claim holds an event back until the
/// visibility window passes, ack drains it, failure rides to dead-letter and replays with a
/// reset attempt counter. (Shared real-postgres fixture; the higher-level delivery semantics
/// live in DeliverySemanticsTests.)
/// </summary>
[Collection("PostgresLog")]
public sealed class PostgresEventQueueTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private readonly EventQueueStore _store;

    public PostgresEventQueueTests(PostgresFixture fx)
    {
        _fx = fx;
        _store = fx.Log;
    }

    public Task InitializeAsync() => _fx.ResetCoreAsync();
    public Task DisposeAsync() => Task.CompletedTask;

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
    public async Task Read_by_run_id_returns_only_that_runs_events_oldest_first()
    {
        // the read side (IEventReader, Block 3.3b): the aggregation source for one reviewer run
        await _store.AppendAsync(new ReviewCommentsSubmitted("o", "r", 1, PrNumber: 1, RunId: 7,
            Comments: [new ReviewFinding("a.cs", 1, "major", "first")]), CancellationToken.None);
        await _store.AppendAsync(new ReviewCommentsSubmitted("o", "r", 1, PrNumber: 1, RunId: 8,
            Comments: [new ReviewFinding("z.cs", 9, "minor", "other run")]), CancellationToken.None);
        await _store.AppendAsync(new ReviewCommentsSubmitted("o", "r", 1, PrNumber: 1, RunId: 7,
            Comments: [new ReviewFinding("b.cs", 2, "minor", "second")]), CancellationToken.None);
        await _store.AppendAsync(new BugReported("o", "r", 1, TaskId: 1, "s", [], "t"), CancellationToken.None);

        var read = await _store.ReadByRunIdAsync<ReviewCommentsSubmitted>(7, CancellationToken.None);

        Assert.Equal(2, read.Count); // only run 7's events, other runs and types excluded
        Assert.Equal("first", read[0].Comments[0].Message);
        Assert.Equal("second", read[1].Comments[0].Message); // appended order preserved
    }

    [Fact]
    public async Task Failed_event_redelivers_then_dead_letters_after_max_attempts()
    {
        var id = await _store.AppendAsync(
            new CommentChanged("o", "r", 1, 10, "a", "b", Deleted: false), CancellationToken.None);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var batch = await _store.ClaimAsync(CommentType, 10, TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.Single(batch); // not expired yet between attempts
            await _store.FailAsync(id, "boom", 3, CancellationToken.None);
            await _fx.ExpireClaimsAsync();
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
