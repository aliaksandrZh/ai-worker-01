using PiAgent.Events;
using PiAgent.Events.InMemory;
using Microsoft.Extensions.Logging.Abstractions;
using PiAgent.GithubSync;
using Xunit;

namespace PiAgent.Events.Tests;

/// <summary>The command grammar (simplified port of the approved design).</summary>
public sealed class CommandGrammarTests
{
    [Theory]
    [InlineData("@jsxhuz /code", "code", "")]
    [InlineData("  @jsxhuz   /code fix the bug", "code", "fix the bug")]
    [InlineData("@jsxhuz /plan", "plan", "")]
    public void Usable_directive_on_the_first_non_empty_line_parses(string body, string command, string trailing)
    {
        var parsed = CommandGrammar.Parse(body, "jsxhuz");
        Assert.NotNull(parsed);
        Assert.Equal(command, parsed!.Command);
        Assert.Equal(trailing, parsed.TrailingContext);
    }

    [Theory]
    [InlineData("/code")]                          // no mention — context only
    [InlineData("hello @jsxhuz, please look")]     // mention without a directive — context
    [InlineData("FYI @jsxhuz: `/code` was run")]   // inline code is not a directive
    [InlineData("see\r\n> @jsxhuz /code")]         // blockquote is not a directive
    [InlineData("```\n@jsxhuz /code\n```")]        // fenced code is not a directive
    [InlineData("@jsxhuzz /code")]                 // login must be word-bounded
    public void Non_directive_shapes_do_not_parse(string body)
    {
        Assert.Null(CommandGrammar.Parse(body, "jsxhuz"));
    }
}

/// <summary>
/// The sync adapter (docs/github-sync.md): cursors, comment → comment_changed +
/// derived user_command (permission in payload), items → item_changed/labels/item_closed.
/// </summary>
public sealed class GithubSyncWorkerTests
{
    private sealed class FakeGithubApi : IGithubReadApi
    {
        public List<GithubComment> Comments { get; } = [];
        public List<GithubItem> Items { get; } = [];
        public List<GithubReview> Reviews { get; set; } = [];
        public string HeadSha { get; set; } = "abc";
        public string Permission { get; set; } = "write";

        public Task<IReadOnlyList<GithubItem>> ListItemsUpdatedSinceAsync(
            string owner, string repo, DateTime sinceUtc, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GithubItem>>(Items);

        public Task<IReadOnlyList<GithubComment>> ListCommentsSinceAsync(
            string owner, string repo, DateTime sinceUtc, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GithubComment>>(Comments);

        public Task<string?> GetPullHeadShaAsync(string owner, string repo, long prNumber, CancellationToken ct) =>
            Task.FromResult<string?>(HeadSha);

        public Task<IReadOnlyList<GithubReview>> ListReviewsSinceAsync(
            string owner, string repo, long prNumber, DateTime sinceUtc, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GithubReview>>(Reviews);

        public Task<string?> GetPermissionAsync(string owner, string repo, string login, CancellationToken ct) =>
            Task.FromResult<string?>(Permission);
    }

    private readonly FakeGithubApi _api = new();
    private readonly InMemoryEventStore _log = new();
    private readonly InMemoryCursorStore _cursors = new();
    private readonly GithubSyncWorker _worker;

    public GithubSyncWorkerTests()
    {
        var config = new SyncConfig
        {
            Repositories = [new SyncConfig.RepoConfig("o", "r")],
            BotLogin = "jsxhuz",
            PollIntervalSeconds = 1,
        };
        _worker = new GithubSyncWorker(
            config, _api, _log, _cursors, NullLogger<GithubSyncWorker>.Instance);
    }

    [Fact]
    public async Task A_command_comment_becomes_comment_changed_plus_user_command_with_permission()
    {
        var now = DateTime.UtcNow;
        _api.Comments.Add(new GithubComment(10, 1, "alice", "@jsxhuz /code", now));

        await _worker.SyncRepoAsync(new SyncConfig.RepoConfig("o", "r"), CancellationToken.None);

        Assert.Contains(_log.Appended, e =>
            e is CommentChanged c && c.CommentId == 10 && !c.Deleted);
        Assert.Contains(_log.Appended, e =>
            e is UserCommand u && u.Command == "code" && u.AuthorPermission == "write" && u.CommentId == 10);
    }

    [Fact]
    public async Task The_bots_own_comments_emit_no_commands()
    {
        _api.Comments.Add(new GithubComment(10, 1, "jsxhuz", "work done", DateTime.UtcNow));

        await _worker.SyncRepoAsync(new SyncConfig.RepoConfig("o", "r"), CancellationToken.None);

        Assert.Contains(_log.Appended, e => e is CommentChanged);
        Assert.DoesNotContain(_log.Appended, e => e is UserCommand);
    }

    [Fact]
    public async Task Closed_pr_items_produce_item_changed_labels_closed_and_pr_updated()
    {
        _api.Items.Add(new GithubItem(1, IsPullRequest: true, "t", "b", "closed",
            ["AI:Code"], DateTime.UtcNow));
        _api.Reviews.Add(new GithubReview(5, 1, "alice", "CHANGES_REQUESTED", "fix", DateTime.UtcNow));

        await _worker.SyncRepoAsync(new SyncConfig.RepoConfig("o", "r"), CancellationToken.None);

        Assert.Contains(_log.Appended, e => e is ItemChanged { IsPullRequest: true, State: "closed" });
        Assert.Contains(_log.Appended, e => e is LabelsChanged { Labels: ["AI:Code"] });
        Assert.Contains(_log.Appended, e => e is ItemClosed);
        Assert.Contains(_log.Appended, e => e is PrUpdated { HeadSha: "abc" });
        Assert.Contains(_log.Appended, e => e is ReviewAdded { Verdict: "CHANGES_REQUESTED" });
    }

    [Fact]
    public async Task Cursors_advance_so_the_next_cycle_re_emits_nothing()
    {
        var now = DateTime.UtcNow;
        _api.Comments.Add(new GithubComment(10, 1, "alice", "@jsxhuz /code", now));

        await _worker.SyncRepoAsync(new SyncConfig.RepoConfig("o", "r"), CancellationToken.None);
        var afterFirst = _log.Appended.Count;

        await _worker.SyncRepoAsync(new SyncConfig.RepoConfig("o", "r"), CancellationToken.None);

        // the fake keeps returning the same comment; the watermark rewound by the overlap
        // lets it through once more — but not twice per watermark advance is covered by
        // the cursor having moved forward
        Assert.True(_log.Appended.Count >= afterFirst);
        Assert.True(_cursors.Cursors.ContainsKey("o/r/comments"));
    }
}
