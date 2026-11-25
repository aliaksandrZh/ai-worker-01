using PiAgent.Events;
using PiAgent.Events.Delivery;
using PiAgent.Events.InMemory;
using Xunit;

namespace PiAgent.Events.Tests;

/// <summary>
/// Delivery semantics of the EventLog contract (docs/eventlog.md invariants) through the
/// in-memory adapter: at-least-once, per-stream head-of-line, dead-letter, replay,
/// transactional append.
/// </summary>
public sealed class DeliverySemanticsTests
{
    private sealed class RecordingConsumer(params string[] failUntilAttempt) : EventConsumer<CommentChanged>
    {
        public List<(CommentChanged Evt, int Attempt)> Seen { get; } = [];

        protected override Task HandleAsync(CommentChanged evt, int attempt, CancellationToken ct)
        {
            Seen.Add((evt, attempt));
            if (attempt < failUntilAttempt.Length + 1 && failUntilAttempt[attempt - 1] == "fail")
                throw new InvalidOperationException("boom");
            return Task.CompletedTask;
        }
    }

    private static async Task<string> PumpOnceAsync(InMemoryEventStore store, IEventConsumer consumer, int maxAttempts = 5)
    {
        var claimed = await store.ClaimAsync(consumer.Consumes.Select(EventRegistry.TypeName).ToArray(), 10,
            TimeSpan.FromSeconds(1), CancellationToken.None);
        foreach (var c in claimed)
        {
            try
            {
                await consumer.HandleAsync(c.Event, c.Attempt, CancellationToken.None);
                await store.AckAsync(c.Id, CancellationToken.None);
            }
            catch (Exception ex)
            {
                await store.FailAsync(c.Id, ex.ToString(), maxAttempts, CancellationToken.None);
            }
        }

        return claimed.Count > 0 ? "processed" : "idle";
    }

    [Fact]
    public async Task Handler_failure_is_redelivered_with_growing_attempt()
    {
        var store = new InMemoryEventStore();
        var consumer = new RecordingConsumer("fail", "ok");
        await store.AppendAsync(new CommentChanged("o", "r", 1, 10, "a", "b", Deleted: false), CancellationToken.None);

        await PumpOnceAsync(store, consumer); // attempt 1 fails
        store.ExpireClaims();
        await PumpOnceAsync(store, consumer); // attempt 2 succeeds

        Assert.Equal([(10, 1), (10, 2)], consumer.Seen.Select(s => (s.Evt.CommentId, s.Attempt)).ToList());
        Assert.Equal(0, store.PendingCount);
    }

    [Fact]
    public async Task Event_moves_to_dead_letter_after_max_attempts_and_is_replayable()
    {
        var store = new InMemoryEventStore();
        var consumer = new RecordingConsumer("fail", "fail", "fail");
        await store.AppendAsync(new CommentChanged("o", "r", 1, 10, "a", "b", Deleted: false), CancellationToken.None);

        for (var i = 0; i < 3; i++)
        {
            store.ExpireClaims();
            await PumpOnceAsync(store, consumer, maxAttempts: 3);
        }

        var dead = await store.ListDeadLettersAsync(10, CancellationToken.None);
        Assert.Single(dead);
        Assert.Equal(3, dead[0].Attempts);
        Assert.Equal(0, store.PendingCount);

        var replayed = await store.ReplayDeadLetterAsync(dead[0].Id, CancellationToken.None);
        Assert.True(replayed);
        Assert.Equal(1, store.PendingCount);
    }

    [Fact]
    public async Task Stream_head_of_line_blocks_later_events()
    {
        var store = new InMemoryEventStore();
        var consumer = new RecordingConsumer("fail"); // always fails attempt 1
        await store.AppendAsync(new CommentChanged("o", "r", 1, 10, "a", "b", Deleted: false), CancellationToken.None);
        await store.AppendAsync(new CommentChanged("o", "r", 1, 11, "a", "b", Deleted: false), CancellationToken.None);

        var delivered = await PumpOnceAsync(store, consumer); // claims the head only

        Assert.Single(consumer.Seen);
        Assert.Equal(10, consumer.Seen[0].Evt.CommentId);
        store.ExpireClaims();
        // the failed head is claimable again; the second event stays blocked behind it
        var secondClaim = await store.ClaimAsync(["CommentChanged"], 10, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Single(secondClaim);
        Assert.Equal(10, ((CommentChanged)secondClaim[0].Event).CommentId);
        _ = delivered;
    }

    [Fact]
    public async Task Parallel_streams_do_not_block_each_other()
    {
        var store = new InMemoryEventStore();
        var consumer = new RecordingConsumer("fail"); // head of stream 1 fails
        await store.AppendAsync(new CommentChanged("o", "r", 1, 10, "a", "b", Deleted: false), CancellationToken.None);
        await store.AppendAsync(new CommentChanged("o", "r", 2, 20, "a", "b", Deleted: false), CancellationToken.None);

        var first = await store.ClaimAsync(["CommentChanged"], 10, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(2, first.Count); // heads of both streams in one batch
        foreach (var c in first.Where(c => ((CommentChanged)c.Event).CommentId == 20))
            await store.AckAsync(c.Id, CancellationToken.None);
        foreach (var c in first.Where(c => ((CommentChanged)c.Event).CommentId == 10))
            await store.FailAsync(c.Id, "boom", 5, CancellationToken.None);

        // next claim returns only stream 1's released head — stream 2 is drained, not blocked
        store.ExpireClaims();
        var second = await store.ClaimAsync(["CommentChanged"], 10, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Single(second);
        Assert.Equal(10, ((CommentChanged)second[0].Event).CommentId);
    }

    [Fact]
    public async Task Transactional_append_commits_atomically()
    {
        var store = new InMemoryEventStore();
        var tx = await store.BeginTransactionAsync(CancellationToken.None);
        await store.AppendAsync(new CommentChanged("o", "r", 1, 10, "a", "b", Deleted: false), tx, CancellationToken.None);
        Assert.Equal(0, store.PendingCount); // nothing visible before commit

        await tx.CommitAsync(CancellationToken.None);
        Assert.Equal(1, store.PendingCount);
    }
}
