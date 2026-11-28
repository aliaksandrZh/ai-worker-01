using PiAgent.Events;
using PiAgent.GithubSync;
using Xunit;

namespace PiAgent.Events.Tests;

/// <summary>
/// Derivation (docs/github-sync.md): the pure half of the sync adapter — observed GitHub state
/// → sync events. No GitHub fake, no event log: records in, records out.
/// </summary>
public sealed class DerivationTests
{
    private static readonly DateTime Now = new(2026, 2, 7, 12, 0, 0, DateTimeKind.Utc);
    private const string Bot = "jsxhuz";

    private static GithubItem Item(string state = "open", bool isPr = false,
        string title = "t", string body = "b", IReadOnlyList<string>? labels = null) =>
        new(25, isPr, title, body, state, labels ?? [], Now);

    [Fact]
    public void An_open_item_yields_item_changed_with_the_full_state_in_the_fingerprint()
    {
        var (changed, closed) = Derivation.ItemEvents("o", "r", Item(labels: ["AI:Code"]));

        Assert.Null(closed);
        var evt = Assert.IsType<RepositoryChanged>(changed!);
        Assert.Equal("o", evt.Owner);
        Assert.Equal(25, evt.Number);
        Assert.Equal(["AI:Code"], evt.Labels);
        // same updated_at second, different content → different eventId
        Assert.NotEqual(Derivation.ItemEvents("o", "r", Item(title: "other")).Changed!.EventId, evt.EventId);
        Assert.Equal(Derivation.ItemEvents("o", "r", Item(labels: ["AI:Code"])).Changed!.EventId, evt.EventId);
    }

    [Fact]
    public void A_closed_item_yields_only_the_identity_keyed_terminal_beat()
    {
        var (changed, closed) = Derivation.ItemEvents("o", "r", Item(state: "closed"));

        Assert.Null(changed);
        var evt = Assert.IsType<RepositoryClosed>(closed!);
        Assert.Equal("item:25:closed", evt.EventId); // identity, not timestamp: re-touching the closed item must dedup to the same beat
    }

    [Fact]
    public void A_comment_yields_comment_changed_plus_its_commands()
    {
        var derived = Derivation.CommentEvents("o", "r", 25,
            new GithubComment(10, "alice", "@jsxhuz /code fix the bug", Now, Now), Bot);

        Assert.IsType<CommentChanged>(derived.Comment);
        Assert.Empty(derived.Decisions);
        var draft = Assert.Single(derived.Commands);
        Assert.Equal("code", draft.Command);
        Assert.Equal("alice", draft.Author);
        Assert.Equal("fix the bug", draft.TrailingContext);
        // the draft is one permission short of the record
        var command = draft.Build("write");
        Assert.IsType<UserCommand>(command);
        Assert.Equal("write", command.AuthorPermission);
        Assert.Equal("code", command.Command);
    }

    [Fact]
    public void Approve_and_changes_become_decisions_not_commands()
    {
        var derived = Derivation.CommentEvents("o", "r", 25,
            new GithubComment(10, "alice", "@jsxhuz /approve\n@jsxhuz /changes", Now, Now), Bot);

        Assert.Empty(derived.Commands);
        Assert.Equal(2, derived.Decisions.Count);
        Assert.Contains(derived.Decisions, d => d.Kind == DecisionKind.PlanApproved);
        Assert.Contains(derived.Decisions, d => d.Kind == DecisionKind.PlanChangesRequested);
    }

    [Fact]
    public void The_bots_own_comments_yield_nothing_but_the_record()
    {
        var derived = Derivation.CommentEvents("o", "r", 25,
            new GithubComment(10, Bot, "@jsxhuz /code", Now, Now), Bot);

        Assert.Empty(derived.Decisions);
        Assert.Empty(derived.Commands);
    }
}
