using PiAgent.Poller.GitHub;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Workers;
using PiAgent.Platform.Http;

namespace PiAgent.Poller.Streams;

/// <summary>
/// `timeline` stream (`issues/{n}/timeline` family): raw per-work-item timeline
/// events feeding WorkItemLink derivation. Visit rule (approved D3): an item is visited when its synced
/// `updated_at_github` moved past the stored visit — closure, cross-references, review requests, and
/// connect/disconnect all bump it. Timelines list ascending by event id and support no `since`; pages
/// are walked until one is fully behind the stored position. Timeline listings deliberately use no ETags
/// (append-only growth can leave page-1 etags stale — risk R3); the position walk keeps the steady-state
/// cost at one request per unchanged item.
///
/// The six in-scope event types persist as `github_event` rows (`source = timeline_api`): raw GitHub
/// names for `cross-referenced`/`closed`/`connected`/`disconnected`, normalized names for
/// `review_requested`/`review_request_removed` (identical strings). Dedupe key `tl:{event_id}` (stable
/// external id; deterministic fingerprint fallback). Links (typed role
/// columns, strict option): `cross-referenced` between a stored same-repository Issue↔PR pair →
/// `related_to` (`github_cross_reference`); `closed` on an ISSUE timeline whose `commit_id` commit
/// message carries closing keywords resolves the introducing merged PR via the ad-hoc
/// `commits/{commit_id}/pulls` read → `closes` (`github_closing_reference`). `connected`/`disconnected`
/// stay raw linkless records; branch names are never consulted anywhere.
///
/// An unresolved link event holds back the item's whole visit — the cursor entry is not written, so
/// the next cycle re-fetches once the counterpart WorkItem syncs (comments-stream boundary pattern;
/// raw rows persist immediately and dedupe keys make re-processing idempotent). HTTP lookups always
/// run outside the page transaction.
/// </summary>
public sealed class TimelineStream : IRepositoryStream
{
    public string StreamName => SyncCursorStreams.Timeline;

    private sealed record WorkItemVisit(Guid Id, long Number, string Type, DateTime UpdatedAtGithub);

    private sealed record LinkWrite(Guid IssueWorkItemId, Guid PrWorkItemId, string Relation, string Source);

    public async Task<StreamResult> SyncAsync(StreamRun run, CancellationToken cancellationToken)
    {
        var cursor = await SyncCursors.GetOrCreateAsync(run.Db, run.Repository.Id, StreamName, cancellationToken);
        var cursorData = TimelineCursor.TryParse(cursor.CursorValue)
                         ?? new TimelineCursor(new Dictionary<long, TimelineItemCursor>());

        try
        {
            var visits = await run.Db.WorkItems.AsNoTracking()
                .OrderBy(w => w.Number)
                .Where(w => w.RepositoryId == run.Repository.Id)
                .Select(w => new WorkItemVisit(w.Id, w.Number, w.Type, w.UpdatedAtGithub))
                .ToListAsync(cancellationToken);

            var entries = cursorData.Items.ToDictionary(kv => kv.Key, kv => kv.Value);
            var anyVisited = false;
            foreach (var item in visits)
            {
                if (entries.TryGetValue(item.Number, out var entry) && item.UpdatedAtGithub <= entry.LastVisit)
                    continue; // nothing changed since the last resolved visit (approved D3 gate)

                var visited = await VisitAsync(run, cursor, entries, item, entry, cancellationToken);
                entries[item.Number] = visited;
                anyVisited = true;
            }

            return new StreamResult(anyVisited ? StreamResultKind.Completed : StreamResultKind.NotModified);
        }
        catch (GitHubRateLimitedException)
        {
            throw; // the cycle must stop for the whole run — nothing advances
        }
        catch (Exception exception)
        {
            return await SyncCursors.RecordFailureAsync(run.Db, run.Repository.Id, StreamName, exception, cancellationToken);
        }
    }

    /// <summary>One work item visit: pages persist raw rows one transaction each; the cursor entry is
    /// written in its own final transaction and ONLY when every page resolved without holdback. A
    /// holdback visit freezes both the visit gate and the position at their old values.</summary>
    private static async Task<TimelineItemCursor> VisitAsync(StreamRun run, SyncCursor cursor,
        Dictionary<long, TimelineItemCursor> entries, WorkItemVisit item, TimelineItemCursor? entry,
        CancellationToken cancellationToken)
    {
        var startPosition = entry?.Position;
        var position = startPosition;
        var holdback = false;
        var requestPath =
            $"repos/{run.Repository.Owner}/{run.Repository.Name}/issues/{item.Number}/timeline?per_page={run.Options.PerPage}";

        while (true)
        {
            var page = await run.Client.GetPageAsync(requestPath, null, cancellationToken);
            var fresh = new List<(JsonElement Element, TimelineEventSnapshot Snapshot, long? Id)>();

            foreach (var element in page.Items)
            {
                var snapshot = GitHubMappers.MapTimelineEvent(element);
                if (snapshot.Id is { } id && position is { } processed && id <= processed)
                    continue; // durably saved in an earlier visit/page
                fresh.Add((element, snapshot, snapshot.Id));
            }

            // Timelines list ASCENDING, so new events sit on the LAST pages — no early stop: every page
            // is walked and the position only skips known events; visit-gating bounds the total cost.

            // HTTP resolution phase first — lookups never run inside the page transaction
            var links = new List<LinkWrite>();
            foreach (var (_, snapshot, _) in fresh)
            {
                var (hold, link) = await ResolveLinkAsync(run, item, snapshot, cancellationToken);
                if (hold)
                    holdback = true;
                if (link is { } write)
                    links.Add(write);
            }

            position = await PersistPageAsync(run, item, fresh, links, position, cancellationToken);
            if (page.NextPageUri is not { } nextPage)
                break;
            requestPath = nextPage.PathAndQuery;
        }

        if (holdback)
            // entry unwritten: the visit gate re-opens next cycle (updated_at still ahead of the stored
            // visit) and the position still points below the unresolved event — dedupe keeps re-walks idempotent
            return entries.TryGetValue(item.Number, out var frozen)
                ? frozen
                : entry ?? new TimelineItemCursor(DateTime.MinValue, null);

        var resolvedPosition = position is { } p ? p : startPosition;
        entries[item.Number] = new TimelineItemCursor(item.UpdatedAtGithub, resolvedPosition);
        await SyncCursorsWriteAsync(run.Db, cursor, entries, cancellationToken);
        return entries[item.Number];
    }

    /// <summary>One page in one transaction: in-scope raw timeline rows + resolved links, plus the
    /// position high-water mark over the page. Events with no link counterpart persist immediately;
    /// only the cursor entry waits for full resolution.</summary>
    private static async Task<long?> PersistPageAsync(StreamRun run, WorkItemVisit item,
        List<(JsonElement Element, TimelineEventSnapshot Snapshot, long? Id)> fresh, List<LinkWrite> links,
        long? position, CancellationToken cancellationToken)
    {
        await using var transaction = await run.Db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var now = DateTime.UtcNow;
            var detections = new List<Detection>();
            foreach (var (_, snapshot, _) in fresh)
            {
                if (!IsPersistable(snapshot))
                    continue;
                detections.Add(new Detection(
                    snapshot.EventType!,
                    GitHubEventSources.TimelineApi,
                    EventKey(item.Number, snapshot),
                    RawPayload(item.Number, snapshot),
                    snapshot.CreatedAt ?? now));
            }
            await EventWriter.WriteAsync(run.Db, run.Repository.Id, item.Id, detections, cancellationToken);

            foreach (var link in links)
                UpsertLink(run.Db, link, now);

            await run.Db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        foreach (var (_, _, id) in fresh)
            if (id is { } known && (position is null || known > position))
                position = known;
        return position;
    }

    private static async Task SyncCursorsWriteAsync(AppDbContext db, SyncCursor cursor,
        Dictionary<long, TimelineItemCursor> entries, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            SyncCursors.RecordSuccess(cursor, new TimelineCursor(entries).Serialize());
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    // ---- link resolution (HTTP phase — outside any transaction) ----

    private static async Task<(bool Holdback, LinkWrite? Link)> ResolveLinkAsync(StreamRun run, WorkItemVisit owner,
        TimelineEventSnapshot snapshot, CancellationToken cancellationToken)
    {
        switch (snapshot.EventType)
        {
            case "cross-referenced":
                return await ResolveCrossReferenceAsync(run, owner, snapshot, cancellationToken);
            case "closed":
                return await ResolveClosingReferenceAsync(run, owner, snapshot, cancellationToken);
            case "connected":
            case "disconnected":
            case "review_requested":
            case "review_request_removed":
            default:
                return (false, null); // raw record only — no link semantics for the first two
        }
    }

    private static async Task<(bool Holdback, LinkWrite? Link)> ResolveCrossReferenceAsync(StreamRun run,
        WorkItemVisit owner, TimelineEventSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.SourceIssueNumber is not { } source)
            return (false, null);

        // same-repository guard: the referencing item must live in our repository
        if (!IsSameRepository(run, snapshot.SourceIssueRepositoryUrl))
            return (false, null);

        // links are Issue↔PR pairs (typed role columns): owner issue + referencing PR, or the mirror
        var ownerIsIssue = owner.Type == WorkItemTypes.Issue;
        var sourceIsPullRequest = snapshot.SourceIsPullRequest == true;
        if (ownerIsIssue != sourceIsPullRequest)
            return (false, null); // same-type pair (issue↔issue or PR↔PR): raw record only

        var prWorkItemId = await LookupWorkItemAsync(run, ownerIsIssue ? source : owner.Number,
            WorkItemTypes.PullRequest, cancellationToken);
        if (prWorkItemId is null)
            return (true, null); // referencing PR not synced yet — boundary holdback
        if (!ownerIsIssue)
        {
            var issueWorkItemId = await LookupWorkItemAsync(run, source, WorkItemTypes.Issue, cancellationToken);
            if (issueWorkItemId is null)
                return (true, null); // referenced issue not synced yet — boundary holdback
            return (false, new LinkWrite(issueWorkItemId.Value, prWorkItemId.Value,
                WorkItemLinkRelations.RelatedTo, WorkItemLinkSources.GithubCrossReference));
        }
        return (false, new LinkWrite(owner.Id, prWorkItemId.Value,
            WorkItemLinkRelations.RelatedTo, WorkItemLinkSources.GithubCrossReference));
    }

    private static async Task<(bool Holdback, LinkWrite? Link)> ResolveClosingReferenceAsync(StreamRun run,
        WorkItemVisit owner, TimelineEventSnapshot snapshot, CancellationToken cancellationToken)
    {
        // closes links come from issue-timeline closure only; a PR's own closed event never links
        if (owner.Type != WorkItemTypes.Issue)
            return (false, null);
        if (snapshot.CommitId is not { } commitId || snapshot.CommitMessage is not { } message
            || !ClosingKeyword.IsMatch(message))
            return (false, null); // manual close without closing keywords — raw record only (D2 option A)

        var introducers = await IntroducersAsync(run, commitId, cancellationToken);
        if (introducers is null)
            return (true, null); // transient commits lookup failure — retry next visit
        if (introducers.Count == 0)
            return (false, null); // direct keyword commit without a PR — links are Issue↔PR only

        var resolved = new List<Guid>();
        foreach (var introducer in introducers)
        {
            var id = await LookupWorkItemAsync(run, introducer.Number, WorkItemTypes.PullRequest, cancellationToken);
            if (id is null)
                return (true, null); // merged PR not synced yet — the pulls stream stores it shortly
            resolved.Add(id.Value);
        }
        return (false, new LinkWrite(owner.Id, resolved[0], WorkItemLinkRelations.Closes,
            WorkItemLinkSources.GithubClosingReference)); // one introducer per closing commit in practice
    }

    private static readonly Regex ClosingKeyword = new(
        @"(?i)\b(close[sd]?|fix(ed|es)?|resolv(e[sd]|es)?)\s*:?\s*#\d+",
        RegexOptions.Compiled);

    /// <summary>Ad-hoc `GET commits/{sha}/pulls` read: the merged PR that introduced the
    /// closing commit. 404 → no associated PR; any other failure → transient holdback; rate limits
    /// propagate to the cycle.</summary>
    private static async Task<List<IntroducerPullRequestSnapshot>?> IntroducersAsync(StreamRun run,
        string commitId, CancellationToken cancellationToken)
    {
        try
        {
            var page = await run.Client.GetPageAsync(
                $"repos/{run.Repository.Owner}/{run.Repository.Name}/commits/{commitId}/pulls?per_page={run.Options.PerPage}",
                null, cancellationToken);
            return page.Items
                .Select(GitHubMappers.MapIntroducerPullRequest)
                .Where(p => p.MergedAt is not null)
                .ToList();
        }
        catch (GitHubRateLimitedException)
        {
            throw;
        }
        catch (GitHubReadException exception) when (exception.Status == HttpStatusCode.NotFound)
        {
            return []; // commit unknown to the repository — no PR link possible
        }
    }

    private static async Task<Guid?> LookupWorkItemAsync(StreamRun run, long number, string type,
        CancellationToken cancellationToken) =>
        await run.Db.WorkItems
            .Where(w => w.RepositoryId == run.Repository.Id && w.Number == number && w.Type == type)
            .Select(w => (Guid?)w.Id)
            .SingleOrDefaultAsync(cancellationToken);

    private static void UpsertLink(AppDbContext db, LinkWrite link, DateTime now)
    {
        var exists = db.WorkItemLinks.Local.Any(l => l.IssueWorkItemId == link.IssueWorkItemId
                                                     && l.PrWorkItemId == link.PrWorkItemId
                                                     && l.RelationType == link.Relation)
                     || db.WorkItemLinks.Any(l => l.IssueWorkItemId == link.IssueWorkItemId
                                                  && l.PrWorkItemId == link.PrWorkItemId
                                                  && l.RelationType == link.Relation);
        if (exists)
            return;
        db.WorkItemLinks.Add(new WorkItemLink
        {
            Id = Guid.NewGuid(),
            IssueWorkItemId = link.IssueWorkItemId,
            PrWorkItemId = link.PrWorkItemId,
            RelationType = link.Relation,
            Source = link.Source,
            CreatedAt = now,
        });
    }

    private static bool IsSameRepository(StreamRun run, string? repositoryUrl)
    {
        if (string.IsNullOrEmpty(repositoryUrl))
            return false; // GitHub reports the referencing issue's repository_url; absence → unknown → no link
        var expected = $"https://api.github.com/repos/{run.Repository.Owner}/{run.Repository.Name}".TrimEnd('/');
        return repositoryUrl.TrimEnd('/').Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    // ---- raw-event persistability, keys, payloads ----

    /// <summary>The six in-scope types persist; everything else only advances the position. Team-only
    /// review requests (no reviewer login) and cross-references with no source item cannot feed any
    /// consumer — they advance the position without persisting.</summary>
    private static bool IsPersistable(TimelineEventSnapshot snapshot) => snapshot.EventType switch
    {
        GitHubEventTypes.ReviewRequested or GitHubEventTypes.ReviewRequestRemoved
            => snapshot.RequestedReviewerLogin is not null,
        GitHubTimelineEvents.CrossReferenced => snapshot.SourceIssueNumber is not null,
        GitHubTimelineEvents.Closed or GitHubTimelineEvents.Connected or GitHubTimelineEvents.Disconnected => true,
        _ => false,
    };

    /// <summary>Stable external event id when present (`tl:{id}`), else a deterministic fingerprint:
    /// owner, event, unix second, and the distinguishing fields.</summary>
    private static string EventKey(long number, TimelineEventSnapshot snapshot) => snapshot.Id is { } id
        ? $"tl:{id}"
        : $"tl:fp:{number}:{snapshot.EventType}:{Unix(snapshot.CreatedAt ?? DateTime.UtcNow)}:{Sha8(string.Join('|',
            snapshot.CommitId, snapshot.SourceIssueNumber, snapshot.RequestedReviewerLogin, snapshot.ActorLogin))}";

    private static object RawPayload(long number, TimelineEventSnapshot snapshot) => snapshot.EventType switch
    {
        GitHubTimelineEvents.CrossReferenced => new TimelineCrossReferencePayload(
            number, snapshot.SourceIssueNumber!.Value,
            snapshot.SourceIsPullRequest == true ? WorkItemTypes.PullRequest : WorkItemTypes.Issue),
        GitHubTimelineEvents.Closed => new TimelineClosedPayload(number, snapshot.CommitId, snapshot.CommitMessage),
        GitHubEventTypes.ReviewRequested or GitHubEventTypes.ReviewRequestRemoved
            => new ReviewRequestEventPayload(number, snapshot.RequestedReviewerLogin!),
        _ => new TimelineRecordPayload(number),
    };

    private static long Unix(DateTime value) => new DateTimeOffset(value, TimeSpan.Zero).ToUnixTimeSeconds();

    private static string Sha8(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }
}
