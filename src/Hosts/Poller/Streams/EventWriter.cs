using PiAgent.Poller.GitHub;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;

namespace PiAgent.Poller.Streams;

/// <summary>
/// Insert-dedupe event writer (02 §7: dedupe_key unique per (source, dedupe_key)): batch keys are checked
/// with one query inside the caller's transaction, then new rows are added with `processing_status =
/// pending`. In-batch duplicates are guarded too. Single-writer poller — no upsert race handling needed.
/// </summary>
public static class EventWriter
{
    public static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> WriteAsync(AppDbContext db, Guid repositoryId, Guid? workItemId,
        IReadOnlyList<Detection> detections, CancellationToken cancellationToken)
    {
        if (detections.Count == 0)
            return 0;

        var keys = detections.Select(d => d.DedupeKey).Distinct().ToList();
        var existing = await db.GitHubEvents
            .Where(e => e.DedupeKey != null && keys.Contains(e.DedupeKey))
            .Select(e => new { e.Source, e.DedupeKey })
            .ToListAsync(cancellationToken);
        var seen = existing.Select(e => (e.Source, e.DedupeKey)).ToHashSet();

        var now = DateTime.UtcNow;
        var added = 0;
        foreach (var detection in detections)
        {
            if (!seen.Add((detection.Source, detection.DedupeKey)))
                continue;
            db.GitHubEvents.Add(new GitHubEvent
            {
                Id = Guid.NewGuid(),
                RepositoryId = repositoryId,
                WorkItemId = workItemId,
                Source = detection.Source,
                DedupeKey = detection.DedupeKey,
                EventType = detection.EventType,
                Payload = JsonSerializer.Serialize(detection.Payload, PayloadOptions),
                OccurredAtGithub = detection.OccurredAt,
                ReceivedAt = now,
                ProcessingStatus = GitHubEventStatuses.Pending,
            });
            added++;
        }
        return added;
    }
}
