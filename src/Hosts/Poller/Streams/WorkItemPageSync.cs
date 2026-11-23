using PiAgent.Poller.GitHub;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Workers;

namespace PiAgent.Poller.Streams;

/// <summary>
/// Per-page persistence shared by the issues and pulls streams (plan task-5 ruling: one shared sink
/// instead of duplicated upsert/detect logic): ONE transaction per page — upsert rows, run snapshot diff
/// against the stored snapshot, write events, advance the cursor — then commit. A crash mid-page rolls
/// the whole transaction back: the cursor stays put and the next overlap window re-processes the page
/// (fingerprint dedupe keeps it idempotent, decision 53).
/// </summary>
public static class WorkItemPageSync
{
    public static async Task<DateTime?> PersistAsync(StreamRun run, SyncCursor cursor,
        IReadOnlyList<WorkItemSnapshot> items, DateTime? previousPosition, DateTime? observedMax,
        CancellationToken cancellationToken)
    {
        await using var transaction = await run.Db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var now = DateTime.UtcNow;
            var numbers = items.Select(i => i.Number).Distinct().ToList();
            var existing = await run.Db.WorkItems
                .AsTracking()
                .Where(w => w.RepositoryId == run.Repository.Id && numbers.Contains(w.Number))
                .ToDictionaryAsync(w => w.Number, cancellationToken);

            var pageMax = observedMax ?? DateTime.MinValue;
            foreach (var item in items)
            {
                if (pageMax < item.UpdatedAt)
                    pageMax = item.UpdatedAt;
                if (!existing.TryGetValue(item.Number, out var row))
                {
                    row = NewRow(run.Repository.Id, item, now);
                    run.Db.WorkItems.Add(row);
                }
                else if (item.UpdatedAt <= row.UpdatedAtGithub)
                {
                    continue; // stale overlap or equal timestamp: stored state is already current
                }

                var before = existing.ContainsKey(item.Number) ? RowToSnapshot(row) : null;
                ApplyRow(row, item, now);

                var detections = SnapshotDiff.Detect(before, item);
                await EventWriter.WriteAsync(run.Db, run.Repository.Id, row.Id, detections, cancellationToken);
            }

            var position = pageMax > (previousPosition ?? DateTime.MinValue) ? pageMax : previousPosition;
            SyncCursors.RecordSuccess(cursor, position is null ? cursor.CursorValue : new TimestampCursor(position).Serialize());
            await run.Db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return position;
        }
        catch
        {
            // rollback only; pending tracked page data is discarded by the failure path
            // (SyncCursors.RecordFailureAsync clears the change tracker)
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    internal static WorkItemSnapshot RowToSnapshot(WorkItem row) => new(
        NodeId: row.GithubNodeId,
        Number: row.Number,
        Type: row.Type,
        Title: row.Title,
        Body: row.Body,
        State: row.State,
        AuthorLogin: row.AuthorLogin,
        Assignees: ParseList(row.Assignees),
        Labels: ParseList(row.Labels),
        RequestedReviewers: ParseList(row.RequestedReviewers),
        HeadSha: row.HeadSha,
        BaseSha: row.BaseSha,
        Draft: row.Draft,
        ClosedAt: row.ClosedAtGithub,
        MergedAt: row.MergedAt,
        UpdatedAt: row.UpdatedAtGithub,
        CreatedAtGithub: row.CreatedAtGithub);

    private static WorkItem NewRow(Guid repositoryId, WorkItemSnapshot item, DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        RepositoryId = repositoryId,
        GithubNodeId = item.NodeId ?? $"number:{item.Number}",
        Number = item.Number,
        Type = item.Type,
        Title = item.Title,
        Body = item.Body,
        State = item.State,
        AuthorLogin = item.AuthorLogin ?? "unknown",
        Assignees = SerializeList(item.Assignees),
        Labels = SerializeList(item.Labels),
        RequestedReviewers = item.RequestedReviewers.Count > 0 ? SerializeList(item.RequestedReviewers) : null,
        HeadSha = item.HeadSha,
        BaseSha = item.BaseSha,
        Draft = item.Draft,
        MergedAt = item.MergedAt,
        CreatedAtGithub = item.CreatedAtGithub,
        UpdatedAtGithub = item.UpdatedAt,
        ClosedAtGithub = item.ClosedAt,
        ClosedAtLocal = item.State == "closed" ? now : null,
        LastSyncedAt = now,
    };

    private static void ApplyRow(WorkItem row, WorkItemSnapshot item, DateTime now)
    {
        row.Title = item.Title;
        row.Body = item.Body;
        row.State = item.State;
        row.Assignees = SerializeList(item.Assignees);
        row.Labels = SerializeList(item.Labels);
        row.CreatedAtGithub = item.CreatedAtGithub;
        row.UpdatedAtGithub = item.UpdatedAt;
        row.ClosedAtGithub = item.ClosedAt;
        // PR-only fields come exclusively from the pulls stream (plan task-5 semantics)
        row.HeadSha = item.HeadSha;
        row.BaseSha = item.BaseSha;
        row.Draft = item.Draft;
        row.MergedAt = item.MergedAt;
        row.RequestedReviewers = item.RequestedReviewers.Count > 0 ? SerializeList(item.RequestedReviewers) : null;
        if (row.ClosedAtLocal is null && item.State == "closed")
            row.ClosedAtLocal = now; // first observation of closure; stays after reopen (02 §3)
        row.LastSyncedAt = now;
    }

    private static string SerializeList(IReadOnlyList<string>? values) =>
        JsonSerializer.Serialize(values ?? [], EventWriter.PayloadOptions);

    private static IReadOnlyList<string> ParseList(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, EventWriter.PayloadOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
