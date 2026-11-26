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
/// The sync adapter (docs/github-sync.md): detection is stateless — each cycle re-reads the
/// working set (open items: full derivation; closed: the ItemClosed beat) and appends
/// content-fingerprinted events; the EventLog's unique (type, eventId) index drops unchanged
/// re-detections, so the fingerprint IS the cursor. No cursor store anywhere.
/// </summary>
public sealed class GithubSyncWorkerTests
{
    private sealed class FakeGithubApi : IGithubReadApi
    {
        public List<GithubItem> Items { get; } = [];
        public Dictionary<long, List<GithubComment>> Comments { get; } = [];
        public List<GithubReview> Reviews { get; set; } = [];
        public string HeadSha { get; set; } = "abc";
        public string Permission { get; set; } = "write";

        public Task<IReadOnlyList<GithubItem>> ListItemsAsync(
            string owner, string repo, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GithubItem>>(Items);

        public Task<IReadOnlyList<GithubComment>> ListCommentsAsync(
            string owner, string repo, long itemNumber, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GithubComment>>(
                Comments.TryGetValue(itemNumber, out var list) ? list : []);

        public Task<string?> GetPullHeadShaAsync(string owner, string repo, long prNumber, CancellationToken ct) =>
            Task.FromResult<string?>(HeadSha);

        public Task<IReadOnlyList<GithubReview>> ListReviewsAsync(
            string owner, string repo, long prNumber, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GithubReview>>(Reviews);

        public Task<string?> GetPermissionAsync(string owner, string repo, string login, CancellationToken ct) =>
            Task.FromResult<string?>(Permission);
    }

    private readonly FakeGithubApi _api = new();
    private readonly InMemoryEventStore _log = new();
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
            config, _api, _log, NullLogger<GithubSyncWorker>.Instance);
    }

    [Fact]
    public async Task A_command_comment_becomes_comment_changed_plus_user_command_with_permission()
    {
        var now = DateTime.UtcNow;
        _api.Comments[1] = [new GithubComment(10, "alice", "@jsxhuz /code", now, now)];
        _api.Items.Add(new GithubItem(1, IsPullRequest: false, "t", "b", "open", [], now));

        await _worker.SyncRepoAsync(new SyncConfig.RepoConfig("o", "r"), CancellationToken.None);

        Assert.Contains(_log.Appended, e =>
            e is CommentChanged c && c.ItemNumber == 1 && c.CommentId == 10 && !c.Deleted);
        Assert.Contains(_log.Appended, e =>
            e is UserCommand u && u.Command == "code" && u.AuthorPermission == "write" && u.CommentId == 10);
    }

    [Fact]
    public async Task The_bots_own_comments_emit_no_commands()
    {
        var now = DateTime.UtcNow;
        _api.Items.Add(new GithubItem(1, IsPullRequest: false, "t", "b", "open", [], now));
        _api.Comments[1] = [new GithubComment(10, "jsxhuz", "work done", now, now)];

        await _worker.SyncRepoAsync(new SyncConfig.RepoConfig("o", "r"), CancellationToken.None);

        Assert.Contains(_log.Appended, e => e is CommentChanged);
        Assert.DoesNotContain(_log.Appended, e => e is UserCommand);
    }

    [Fact]
    public async Task An_open_pr_produces_item_changed_labels_pr_updated_and_reviews()
    {
        var now = DateTime.UtcNow;
        _api.Items.Add(new GithubItem(1, IsPullRequest: true, "t", "b", "open",
            ["AI:Code"], now));
        _api.Reviews = [new GithubReview(5, 1, "alice", "CHANGES_REQUESTED", "fix", now)];

        await _worker.SyncRepoAsync(new SyncConfig.RepoConfig("o", "r"), CancellationToken.None);

        Assert.Contains(_log.Appended, e =>
            e is ItemChanged { IsPullRequest: true, State: "open", Labels: ["AI:Code"] });
        Assert.Contains(_log.Appended, e => e is PrUpdated { HeadSha: "abc" });
        Assert.Contains(_log.Appended, e => e is ReviewAdded { Verdict: "CHANGES_REQUESTED" });
        Assert.DoesNotContain(_log.Appended, e => e is ItemClosed);
    }

    [Fact]
    public async Task A_closed_item_gets_only_its_terminal_beat()
    {
        var now = DateTime.UtcNow;
        _api.Items.Add(new GithubItem(1, IsPullRequest: true, "t", "b", "closed",
            ["AI:Code"], now));
        _api.Reviews = [new GithubReview(5, 1, "alice", "CHANGES_REQUESTED", "fix", now)];
        _api.Comments[1] = [new GithubComment(10, "alice", "@jsxhuz /code", now, now)];

        await _worker.SyncRepoAsync(new SyncConfig.RepoConfig("o", "r"), CancellationToken.None);

        Assert.Contains(_log.Appended, e => e is ItemClosed);
        Assert.DoesNotContain(_log.Appended, e => e is ItemChanged);
        Assert.DoesNotContain(_log.Appended, e => e is PrUpdated);
        Assert.DoesNotContain(_log.Appended, e => e is ReviewAdded);
        Assert.DoesNotContain(_log.Appended, e => e is CommentChanged);
        Assert.DoesNotContain(_log.Appended, e => e is UserCommand);
    }

    [Fact]
    public async Task An_edit_within_the_same_second_passes_because_the_fingerprint_covers_the_content()
    {
        var now = DateTime.UtcNow;
        _api.Items.Add(new GithubItem(1, IsPullRequest: false, "original", "b", "open", [], now));

        await _worker.SyncRepoAsync(new SyncConfig.RepoConfig("o", "r"), CancellationToken.None);

        // the edit keeps the same updated_at second — the content hash in the eventId is
        // what distinguishes it
        _api.Items.Clear();
        _api.Items.Add(new GithubItem(1, IsPullRequest: false, "edited", "b", "open", [], now));

        await _worker.SyncRepoAsync(new SyncConfig.RepoConfig("o", "r"), CancellationToken.None);

        Assert.Equal(2, _log.Appended.Count(e => e is ItemChanged));
    }

    [Fact]
    public async Task A_label_change_is_an_item_change_and_an_unchanged_label_re_emits_nothing()
    {
        var now = DateTime.UtcNow;
        _api.Items.Add(new GithubItem(1, IsPullRequest: false, "t", "b", "open", ["AI:Code"], now));

        await _worker.SyncRepoAsync(new SyncConfig.RepoConfig("o", "r"), CancellationToken.None);

        _api.Items.Clear();
        _api.Items.Add(new GithubItem(1, IsPullRequest: false, "t", "b", "open", ["AI:Code", "AI:Plan"], now));

        await _worker.SyncRepoAsync(new SyncConfig.RepoConfig("o", "r"), CancellationToken.None);
        Assert.Equal(2, _log.Appended.Count(e => e is ItemChanged));

        // and a title edit does not re-emit the item for its unchanged labels — the
        // fingerprint covers everything at once
        _api.Items.Clear();
        _api.Items.Add(new GithubItem(1, IsPullRequest: false, "t", "b", "open", ["AI:Code", "AI:Plan"], now));

        await _worker.SyncRepoAsync(new SyncConfig.RepoConfig("o", "r"), CancellationToken.None);
        Assert.Equal(2, _log.Appended.Count(e => e is ItemChanged));
    }

    [Fact]
    public async Task The_next_cycle_re_emits_nothing()
    {
        var now = DateTime.UtcNow;
        _api.Items.Add(new GithubItem(1, IsPullRequest: false, "t", "b", "open", [], now));
        _api.Comments[1] = [new GithubComment(10, "alice", "@jsxhuz /code", now, now)];

        await _worker.SyncRepoAsync(new SyncConfig.RepoConfig("o", "r"), CancellationToken.None);
        var afterFirst = _log.Appended.Count;

        // the fake keeps returning the same working set; the fingerprint dedup drops the
        // re-detection — there is no cursor to rewind and no window to let it through
        await _worker.SyncRepoAsync(new SyncConfig.RepoConfig("o", "r"), CancellationToken.None);

        Assert.Equal(afterFirst, _log.Appended.Count);
    }
}
