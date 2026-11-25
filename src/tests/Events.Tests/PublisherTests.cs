using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiAgent.Events;
using PiAgent.Events.InMemory;
using PiAgent.Publisher;
using Xunit;

namespace PiAgent.Events.Tests;

/// <summary>
/// The publication consumer (docs/publisher.md): key-once, transient → redelivery,
/// permanent → poison (straight to dead-letter), no label writes.
/// </summary>
public sealed class PublisherTests
{
    private sealed class FakeGithubApi : IGithubWriteApi
    {
        public List<PublishComment> Comments { get; } = [];
        public List<PublishPr> Prs { get; } = [];
        public List<PublishReview> Reviews { get; } = [];
        public List<PublishBoardMove> BoardMoves { get; } = [];
        public GithubExceptionKind FailWith { get; set; } = GithubExceptionKind.None;

        public Task<long> CreateCommentAsync(PublishComment evt, CancellationToken ct)
        {
            Comments.Add(evt);
            return FailOrReturnAsync();
        }

        public Task<long> CreateOrUpdatePrAsync(PublishPr evt, CancellationToken ct)
        {
            Prs.Add(evt);
            return FailOrReturnAsync();
        }

        public Task<long> SubmitReviewAsync(PublishReview evt, CancellationToken ct)
        {
            Reviews.Add(evt);
            return FailOrReturnAsync();
        }

        public Task MoveBoardItemAsync(PublishBoardMove evt, CancellationToken ct)
        {
            BoardMoves.Add(evt);
            return FailOrReturnAsync();
        }

        private Task<long> FailOrReturnAsync() => FailWith switch
        {
            GithubExceptionKind.Transient => throw new GithubTransientException("rate limit", 30),
            GithubExceptionKind.Permanent => throw new GithubPermanentException("gone"),
            _ => Task.FromResult(42L),
        };
    }

    private enum GithubExceptionKind { None, Transient, Permanent }

    private sealed class InMemoryMarkerStore : IPublishMarkerStore
    {
        public HashSet<string> Keys { get; } = [];

        public Task<bool> ExistsAsync(string idempotencyKey, CancellationToken ct) =>
            Task.FromResult(Keys.Contains(idempotencyKey));

        public Task InsertAsync(string idempotencyKey, IEventTransaction? tx, CancellationToken ct)
        {
            Keys.Add(idempotencyKey);
            return Task.CompletedTask;
        }
    }

    private readonly FakeGithubApi _github = new();
    private readonly InMemoryMarkerStore _markers = new();
    private readonly InMemoryEventStore _log = new();
    private readonly PublisherConsumer _publisher;

    public PublisherTests()
    {
        _publisher = new PublisherConsumer(
            _markers, _github, NullLogger<PublisherConsumer>.Instance);
    }

    [Fact]
    public async Task Same_key_publishes_once()
    {
        var comment = new PublishComment(PublishCommentKind.Plan, "o", "r", 1,
            IdempotencyKey: "plan:1", Body: "plan");
        await _publisher.HandleAsync(comment, 1, CancellationToken.None);
        await _publisher.HandleAsync(comment, 2, CancellationToken.None); // redelivery

        Assert.Single(_github.Comments);
        Assert.Equal(["plan:1"], _markers.Keys);
    }

    [Fact]
    public async Task Transient_failure_throws_for_redelivery()
    {
        _github.FailWith = GithubExceptionKind.Transient;
        var comment = new PublishComment(PublishCommentKind.Notification, "o", "r", 1,
            IdempotencyKey: "notification:1", Body: "n");

        await Assert.ThrowsAsync<GithubTransientException>(() => _publisher.HandleAsync(comment, 1, CancellationToken.None));
        Assert.Empty(_markers.Keys); // not marked → redelivery retries
    }

    [Fact]
    public async Task Permanent_failure_poisons_the_event_without_retries()
    {
        var queue = _log;
        var id = await queue.AppendAsync(
            new PublishReview("o", "r", 1, "APPROVE", "ok", RunId: 1, IdempotencyKey: "review:1"),
            CancellationToken.None);
        _github.FailWith = GithubExceptionKind.Permanent;

        var claimed = await queue.ClaimAsync(["PublishReview"], 10, TimeSpan.FromSeconds(1), CancellationToken.None);
        try
        {
            await _publisher.HandleAsync(claimed[0].Event, 1, CancellationToken.None);
            Assert.Fail("expected permanent failure");
        }
        catch (PermanentDeliveryException)
        {
            await queue.PoisonAsync(claimed[0].Id, "gone", CancellationToken.None);
        }

        var dead = await queue.ListDeadLettersAsync(10, CancellationToken.None);
        Assert.Single(dead);
        Assert.Equal(0, queue.PendingCount); // no retries scheduled
    }

    [Fact]
    public async Task Board_move_without_a_synced_card_is_skipped()
    {
        await _publisher.HandleAsync(
            new PublishBoardMove("o", 0, "", "review", RunId: 1, IdempotencyKey: "board:1"), 1, CancellationToken.None);

        Assert.Empty(_github.BoardMoves);
        Assert.Empty(_markers.Keys);
    }
}
