using System.Security.Cryptography;
using System.Text;
using PiAgent.Events;

namespace PiAgent.GithubSync;

/// <summary>
/// The derivation half of the sync adapter (docs/github-sync.md): observed GitHub state →
/// sync events. Pure — no GitHub calls, no appends; the worker walks the API and emits what
/// this module builds. The one thing it cannot build is a full `UserCommand`: the record
/// requires the author's resolved permission, and resolving it is an HTTP read — so commands
/// leave here as drafts the worker completes and emits.
/// </summary>
internal static class Derivation
{
    /// <summary>The events one item's observation stands for: ItemChanged while open, the
    /// ItemClosed terminal beat once it isn't.</summary>
    public static (RepositoryChanged? Changed, RepositoryClosed? Closed) ItemEvents(string owner, string repo, GithubItem item)
    {
        var at = Unix(item.UpdatedAtUtc);
        var itemId = $"item:{item.Number}:{at}:{Sha8($"{item.Title}\n{item.Body}\n{item.State}\n{string.Join(',', item.Labels)}")}";

        if (item.State != "open")
        {
            // the terminal beat: identity, not timestamp — a closed item keeps getting touched
            // (comments on a closed issue bump updated_at and float it back onto page 1), and
            // every one of those cycles must dedup to the same beat, not re-close an already
            // closed Workflow.
            return (null, new RepositoryClosed
            {
                Owner = owner,
                Repo = repo,
                Number = item.Number,
                EventId = $"item:{item.Number}:closed",
            });
        }

        // one record per item: title, body, state and labels in the payload and in the
        // fingerprint — a label change is an item change, and an edit of anything else no
        // longer re-emits a labels event with unchanged labels
        return (new RepositoryChanged
        {
            Owner = owner,
            Repo = repo,
            Number = item.Number,
            IsPullRequest = item.IsPullRequest,
            Title = item.Title,
            Body = item.Body,
            State = item.State,
            Labels = item.Labels,
            EventId = itemId,
        }, null);
    }

    public static PrUpdated? PrEvent(
        string owner, string repo, long prNumber, DateTime itemUpdatedAtUtc, string headSha) =>
        new PrUpdated
        {
            Owner = owner,
            Repo = repo,
            PrNumber = prNumber,
            HeadSha = headSha,
            EventId = $"pr:{prNumber}:{Unix(itemUpdatedAtUtc)}:{headSha[..Math.Min(12, headSha.Length)]}",
        };

    public static ReviewAdded? ReviewEvent(
        string owner, string repo, long prNumber, GithubReview review) =>
        new ReviewAdded
        {
            Owner = owner,
            Repo = repo,
            PrNumber = prNumber,
            ReviewId = review.Id,
            Author = review.Author,
            Verdict = review.Verdict,
            Body = review.Body,
            EventId = $"review:{review.Id}",
        };

    /// <summary>The events one comment's observation stands for: the CommentChanged record
    /// always; the parsed directives — decisions at the edge (docs/flows events.md
    /// "user_decision") and command drafts the worker resolves permission for.</summary>
    public static CommentEvents CommentEvents(
        string owner, string repo, long itemNumber, GithubComment comment, string botLogin)
    {
        var at = Unix(comment.UpdatedAtUtc);
        var commentEvent = new CommentChanged
        {
            Owner = owner,
            Repo = repo,
            ItemNumber = itemNumber,
            CommentId = comment.Id,
            Author = comment.Author,
            Body = comment.Body,
            Deleted = false,
            EventId = $"comment:{comment.Id}:{at}:{Sha8(comment.Body)}",
        };

        var decisions = new List<UserDecision>();
        var commands = new List<CommandDraft>();

        if (comment.Author.Equals(botLogin, StringComparison.Ordinal))
            return new(commentEvent, decisions, commands); // the bot's own comments are noise

        foreach (var parsed in CommandGrammar.ParseAll(comment.Body, botLogin))
        {
            switch (parsed.Command)
            {
                case "approve":
                    decisions.Add(new UserDecision
                    {
                        Kind = DecisionKind.PlanApproved,
                        Owner = owner,
                        Repo = repo,
                        ItemNumber = itemNumber,
                        Author = comment.Author,
                        Text = parsed.TrailingContext,
                        EventId = $"decision:{comment.Id}:{at}:{Sha8(comment.Body)}",
                    });
                    break;
                case "changes":
                    decisions.Add(new UserDecision
                    {
                        Kind = DecisionKind.PlanChangesRequested,
                        Owner = owner,
                        Repo = repo,
                        ItemNumber = itemNumber,
                        Author = comment.Author,
                        Text = parsed.TrailingContext,
                        EventId = $"decision:{comment.Id}:{at}:{Sha8(comment.Body)}",
                    });
                    break;
                default:
                    commands.Add(new CommandDraft(parsed.Command, owner, repo, itemNumber,
                        comment.Id, comment.Author, parsed.TrailingContext,
                        $"command:{comment.Id}:{at}:{parsed.Command}:{Sha8(comment.Body)}"));
                    break;
            }
        }

        return new(commentEvent, decisions, commands);
    }

    private static long Unix(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds();

    /// <summary>main's Sha8: a content fingerprint that distinguishes edits within the same second.</summary>
    private static string Sha8(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8].ToLowerInvariant();
}

/// <summary>A derived UserCommand minus its permission — the one field that costs an HTTP read;
/// the worker resolves it and Build completes the record.</summary>
internal sealed record CommandDraft(
    string Command,
    string Owner,
    string Repo,
    long ItemNumber,
    long CommentId,
    string Author,
    string TrailingContext,
    string EventId)
{
    public UserCommand Build(string permission) => new UserCommand
    {
        Command = Command,
        Owner = Owner,
        Repo = Repo,
        ItemNumber = ItemNumber,
        CommentId = CommentId,
        Author = Author,
        AuthorPermission = permission,
        Text = TrailingContext,
        EventId = EventId,
    };
}

internal sealed record CommentEvents(
    CommentChanged Comment,
    IReadOnlyList<UserDecision> Decisions,
    IReadOnlyList<CommandDraft> Commands);
