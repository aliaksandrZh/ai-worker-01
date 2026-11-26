using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PiAgent.Events;
using PiAgent.Publisher;
using PiAgent.Publisher.Postgres;
using PiAgent.Tests.Postgres;
using Xunit;

namespace PiAgent.Events.Tests;

/// <summary>
/// The publication consumer (docs/publisher.md): key-once, transient → redelivery,
/// permanent → poison (straight to dead-letter), no label writes. Against the real
/// Postgres marker store.
/// </summary>
[Collection("PostgresLog")]
public sealed class PublisherTests : IAsyncLifetime
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

    private readonly PostgresFixture _fx;
    private readonly FakeGithubApi _github = new();
    private readonly PostgresPublishMarkerStore _markers;
    private readonly PublisherConsumer _publisher;

    public PublisherTests(PostgresFixture fx)
    {
        _fx = fx;
        _markers = new PostgresPublishMarkerStore(fx.DataSource, NullLogger<PostgresPublishMarkerStore>.Instance);
        _publisher = new PublisherConsumer(
            _markers, _github, _fx.Log, NullLogger<PublisherConsumer>.Instance);
    }

    public async Task InitializeAsync()
    {
        await _fx.ResetCoreAsync();
        await _markers.EnsureSchemaAsync(CancellationToken.None);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<IReadOnlyList<string>> MarkerKeysAsync()
    {
        await using var cmd = _fx.DataSource.CreateCommand("SELECT key FROM publish_marker ORDER BY key");
        await using var reader = await cmd.ExecuteReaderAsync();
        var keys = new List<string>();
        while (await reader.ReadAsync())
            keys.Add(reader.GetString(0));
        return keys;
    }

    [Fact]
    public async Task Same_key_publishes_once()
    {
        var comment = new PublishComment(PublishCommentKind.Plan, "o", "r", 1,
            IdempotencyKey: "plan:1", Body: "plan");
        await _publisher.HandleAsync(comment, 1, CancellationToken.None);
        await _publisher.HandleAsync(comment, 2, CancellationToken.None); // redelivery

        Assert.Single(_github.Comments);
        Assert.Equal(["plan:1"], await MarkerKeysAsync());
    }

    [Fact]
    public async Task Transient_failure_throws_for_redelivery()
    {
        _github.FailWith = GithubExceptionKind.Transient;
        var comment = new PublishComment(PublishCommentKind.Notification, "o", "r", 1,
            IdempotencyKey: "notification:1", Body: "n");

        await Assert.ThrowsAsync<GithubTransientException>(() => _publisher.HandleAsync(comment, 1, CancellationToken.None));
        Assert.Empty(await MarkerKeysAsync()); // not marked → redelivery retries
    }

    [Fact]
    public async Task Permanent_failure_poisons_the_event_without_retries()
    {
        var queue = _fx.Log;
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
        Assert.Equal(0, await _fx.PendingCountAsync()); // no retries scheduled
    }

    [Fact]
    public async Task Board_move_without_a_synced_card_is_skipped()
    {
        await _publisher.HandleAsync(
            new PublishBoardMove("o", 0, "", "review", RunId: 1, IdempotencyKey: "board:1"), 1, CancellationToken.None);

        Assert.Empty(_github.BoardMoves);
        Assert.Empty(await MarkerKeysAsync());
    }

    [Fact]
    public async Task Pr_publish_announces_the_real_pr_number()
    {
        // Case B: the review runs need the number GitHub assigned, not the item number
        var pr = new PublishPr(PublishPrKind.Create, "o", "r", WorkflowId: 1, ItemNumber: 27,
            IdempotencyKey: "pr:1:coder:1", HeadBranch: "agent/1-fix", BaseBranch: "main",
            Title: "t", Body: "b", PrNumber: null);

        await _publisher.HandleAsync(pr, 1, CancellationToken.None);

        Assert.Contains(await _fx.AppendedAsync(),
            e => e is PrPublished { Owner: "o", Repo: "r", ItemNumber: 27, PrNumber: 42 });
    }

    [Fact]
    public async Task Pr_publish_redelivery_does_not_re_announce_the_pr_number()
    {
        var pr = new PublishPr(PublishPrKind.Create, "o", "r", WorkflowId: 1, ItemNumber: 27,
            IdempotencyKey: "pr:1:coder:1", HeadBranch: "agent/1-fix", BaseBranch: "main",
            Title: "t", Body: "b", PrNumber: null);
        await _publisher.HandleAsync(pr, 1, CancellationToken.None);

        await _publisher.HandleAsync(pr, 2, CancellationToken.None); // redelivery

        Assert.Single((await _fx.AppendedAsync()).OfType<PrPublished>());
    }
}
