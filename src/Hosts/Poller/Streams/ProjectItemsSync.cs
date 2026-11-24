using PiAgent.Poller.GitHub;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Http;

namespace PiAgent.Poller.Streams;

/// <summary>
/// The Projects v2 pass ("sync + logs" scope): for every configured project
/// (deploy/config/projects.yaml) one authoritative full listing per pass — GraphQL
/// `user.projectV2.items`, because the REST `/projectsV2/{n}/items` endpoint does not return field
/// values (only the built-in Title) and column states — the Status single-select included — are
/// GraphQL-only. Detection is list-diff against `project_item` snapshots:
/// added / field-changed / removed items emit `project_item_*` events (deduped, written
/// `pending`) for content that maps to a tracked repository; unmappable content (DraftIssue,
/// untracked repository) is synced and logged only. One transaction per pass — a crash rolls
/// everything back and the next pass re-detects (fingerprint dedupe keeps it idempotent).
/// Event processing lives in the Orchestrator; the pass keeps no cursor beyond the
/// bookkeeping on its project_sync row (the legacy ETag column stays, no longer written).
/// </summary>
public static class ProjectItemsSync
{
    private static readonly JsonSerializerOptions FieldOptions = new(JsonSerializerDefaults.Web);

    public static async Task<StreamResult> SyncAsync(AppDbContext db, GitHubReadClient client, ProjectRef project,
        ILogger logger, CancellationToken cancellationToken)
    {
        var state = await GetOrCreateStateAsync(db, project, cancellationToken);
        try
        {
            var result = await SyncListedAsync(db, client, project, state, logger, cancellationToken);
            RecordSuccess(state);
            await db.SaveChangesAsync(cancellationToken);
            return result;
        }
        catch (GitHubRateLimitedException)
        {
            throw; // the cycle must stop for the whole run — nothing advances
        }
        catch (Exception exception)
        {
            db.ChangeTracker.Clear();
            state = await GetOrCreateStateAsync(db, project, cancellationToken);
            RecordFailure(state, exception.Message);
            await db.SaveChangesAsync(cancellationToken);
            return new StreamResult(StreamResultKind.Failed, exception.Message);
        }
    }

    private static async Task<StreamResult> SyncListedAsync(AppDbContext db, GitHubReadClient client,
        ProjectRef project, ProjectSync state, ILogger logger, CancellationToken cancellationToken)
    {
        var board = $"{project.Owner}/{project.Number}";

        // 1. Full listing: cursor-paginated GraphQL (field values are GraphQL-only — see the class doc).
        var listed = new List<ProjectItemSnapshot>();
        string? cursor = null;
        while (true)
        {
            var page = await client.ListProjectItemsAsync(project.Owner, project.Number, cursor, cancellationToken);
            listed.AddRange(page.Nodes.Select(GitHubMappers.MapProjectItemGraphQL));
            if (!page.HasNextPage)
                break;
            cursor = page.EndCursor;
        }

        // 2. One transaction per pass: diff against the stored snapshots, persist rows + events.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var now = DateTime.UtcNow;
            var stored = await db.ProjectItems
                .Where(i => i.ProjectSyncId == state.Id)
                .ToDictionaryAsync(i => i.ItemId, cancellationToken);
            // full_name is stored normalized lowercase; GraphQL nameWithOwner carries the real casing
            var repositories = await db.Repositories.AsNoTracking()
                .ToDictionaryAsync(r => r.FullName, r => r.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);
            var listedIds = listed.Select(s => s.ItemId).ToHashSet();

            var added = 0;
            var updated = 0;
            var removed = 0;

            // Removals: the authoritative listing no longer carries the stored item.
            foreach (var (itemId, row) in stored.Where(kv => !listedIds.Contains(kv.Key)))
            {
                db.ProjectItems.Remove(row);
                removed++;
                await WriteEventAsync(db, project, board, itemId, row.RepositoryId, row.RepositoryFullName,
                    row.Number, logger, "removed",
                    new Detection(GitHubEventTypes.ProjectItemRemoved, GitHubEventSources.ProjectItemsSync,
                        $"pi:{board}:item:{itemId}:removed",
                        new ProjectItemEventPayload(board, itemId, row.ContentType, row.Number, row.RepositoryFullName,
                            Changes: null),
                        now),
                    cancellationToken);
            }

            foreach (var snapshot in listed)
            {
                var repositoryId = snapshot.RepositoryFullName is { } fullName
                    && repositories.TryGetValue(fullName, out var id)
                    ? (Guid?)id
                    : null;

                if (!stored.TryGetValue(snapshot.ItemId, out var row))
                {
                    db.ProjectItems.Add(NewRow(state.Id, snapshot, repositoryId, now));
                    added++;
                    await WriteEventAsync(db, project, board, snapshot.ItemId, repositoryId,
                        snapshot.RepositoryFullName, snapshot.Number, logger, "added",
                        new Detection(GitHubEventTypes.ProjectItemAdded, GitHubEventSources.ProjectItemsSync,
                            $"pi:{board}:item:{snapshot.ItemId}:added",
                            new ProjectItemEventPayload(board, snapshot.ItemId, snapshot.ContentType, snapshot.Number,
                                snapshot.RepositoryFullName, Changes: null),
                            snapshot.UpdatedAt),
                        cancellationToken);
                    continue;
                }
                if (snapshot.UpdatedAt < row.UpdatedAtGithub)
                    continue; // stale overlap: the stored state is already current
                if (snapshot.UpdatedAt == row.UpdatedAtGithub)
                {
                    // Already-current content: coordinates only — heals a stale link (e.g. a row
                    // written while the repository lookup failed) without any event.
                    ApplyRow(row, snapshot, repositoryId, now);
                    continue;
                }

                var changes = FieldChanges(row, snapshot);
                if (changes.Count == 0)
                {
                    ApplyRow(row, snapshot, repositoryId, now); // coordinates only, no event
                    continue;
                }
                updated++;
                var pairs = string.Join(";", changes.Select(kv => $"{kv.Key}={kv.Value}"));
                await WriteEventAsync(db, project, board, snapshot.ItemId, repositoryId,
                    snapshot.RepositoryFullName, snapshot.Number, logger, "updated",
                    new Detection(GitHubEventTypes.ProjectItemUpdated, GitHubEventSources.ProjectItemsSync,
                        $"pi:{board}:item:{snapshot.ItemId}:upd:{Unix(snapshot.UpdatedAt)}:{Sha8(pairs)}",
                        new ProjectItemEventPayload(board, snapshot.ItemId, snapshot.ContentType, snapshot.Number,
                            snapshot.RepositoryFullName, changes),
                        snapshot.UpdatedAt),
                    cancellationToken);
                ApplyRow(row, snapshot, repositoryId, now);
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("project {project} synced: {added} added, {updated} updated, {removed} removed",
                board, added, updated, removed);
            return new StreamResult(StreamResultKind.Completed);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>Events flow only for content that maps to a tracked repository (GitHubEvent
    /// carries a repository FK); the log line covers every item either way.</summary>
    private static async Task WriteEventAsync(AppDbContext db, ProjectRef project, string board, long itemId,
        Guid? repositoryId, string? repositoryFullName, int? number, ILogger logger, string change,
        Detection detection, CancellationToken cancellationToken)
    {
        if (repositoryId is not { } repo)
        {
            logger.LogInformation("project item {change}: {project} item {item_id} ({label})",
                change, board, itemId, repositoryFullName ?? "unmapped content");
            return;
        }

        var workItemLabel = "without a tracked work item";
        Guid? workItemId = null;
        if (number is { } value)
        {
            workItemId = await db.WorkItems
                .Where(w => w.RepositoryId == repo && w.Number == value)
                .Select(w => (Guid?)w.Id)
                .FirstOrDefaultAsync(cancellationToken);
            workItemLabel = workItemId is { } ? $"{repositoryFullName}#{value}" : $"{repositoryFullName}#{value} (no work item yet)";
        }
        logger.LogInformation("project item {change}: {project} item {item_id} → {label}",
            change, board, itemId, workItemLabel);
        await EventWriter.WriteAsync(db, repo, workItemId, [detection], cancellationToken);
    }

    /// <summary>Field-level change map keyed by field name (`Status: Backlog -> Ready`); the
    /// title participates as its own entry. Re-parses both the stored and the new field lists.</summary>
    private static Dictionary<string, string> FieldChanges(ProjectItem row, ProjectItemSnapshot snapshot)
    {
        var changes = new Dictionary<string, string>();
        if (!string.Equals(row.Title, snapshot.Title, StringComparison.Ordinal))
            changes["title"] = $"{row.Title} -> {snapshot.Title}";
        var before = JsonSerializer.Deserialize<List<ProjectFieldValue>>(row.FieldValues, FieldOptions) ?? [];
        var after = snapshot.Fields;
        foreach (var field in after.Where(f => before.All(b => b.Id != f.Id)))
            changes[field.Name ?? $"field:{field.Id}"] = $"<unset> -> {field.Value}";
        foreach (var field in before.Where(b => after.All(a => a.Id != b.Id)))
            changes[field.Name ?? $"field:{field.Id}"] = $"{field.Value} -> <unset>";
        foreach (var field in after)
        {
            var previous = before.FirstOrDefault(b => b.Id == field.Id);
            if (previous is not { } old || old.Value == field.Value)
                continue;
            changes[old.Name ?? field.Name ?? $"field:{field.Id}"] = $"{old.Value} -> {field.Value}";
        }
        return changes;
    }

    private static ProjectItem NewRow(Guid projectSyncId, ProjectItemSnapshot snapshot, Guid? repositoryId,
        DateTime now) => new()
    {
        Id = Guid.NewGuid(),
        ProjectSyncId = projectSyncId,
        ItemId = snapshot.ItemId,
        ItemNodeId = snapshot.ItemNodeId,
        ContentType = snapshot.ContentType,
        RepositoryId = repositoryId,
        RepositoryFullName = snapshot.RepositoryFullName,
        Number = snapshot.Number,
        Title = snapshot.Title,
        FieldValues = JsonSerializer.Serialize(snapshot.Fields, FieldOptions),
        CreatedAtGithub = snapshot.CreatedAt,
        UpdatedAtGithub = snapshot.UpdatedAt,
        LastSyncedAt = now,
    };

    private static void ApplyRow(ProjectItem row, ProjectItemSnapshot snapshot, Guid? repositoryId, DateTime now)
    {
        row.ItemNodeId = snapshot.ItemNodeId;
        row.ContentType = snapshot.ContentType;
        row.RepositoryId = repositoryId;
        row.RepositoryFullName = snapshot.RepositoryFullName;
        row.Number = snapshot.Number;
        row.Title = snapshot.Title;
        row.FieldValues = JsonSerializer.Serialize(snapshot.Fields, FieldOptions);
        row.CreatedAtGithub = snapshot.CreatedAt;
        row.UpdatedAtGithub = snapshot.UpdatedAt;
        row.LastSyncedAt = now;
    }

    public static async Task<ProjectSync> GetOrCreateStateAsync(AppDbContext db, ProjectRef project,
        CancellationToken cancellationToken)
    {
        var existing = await db.ProjectSyncs
            .SingleOrDefaultAsync(p => p.Owner == project.Owner && p.ProjectNumber == project.Number,
                cancellationToken);
        if (existing is not null)
            return existing;

        var state = new ProjectSync
        {
            Id = Guid.NewGuid(),
            Owner = project.Owner,
            ProjectNumber = project.Number,
            UpdatedAt = DateTime.UtcNow,
        };
        db.ProjectSyncs.Add(state);
        await db.SaveChangesAsync(cancellationToken);
        return state;
    }

    /// <summary>Durable-success bookkeeping (ETag included) — called after the pass committed.</summary>
    private static void RecordSuccess(ProjectSync state)
    {
        state.LastSuccessAt = DateTime.UtcNow;
        state.LastAttemptAt = state.LastSuccessAt;
        state.LastError = null;
        state.UpdatedAt = state.LastSuccessAt!.Value;
    }

    /// <summary>A failed attempt never loses the previous ETag — last_attempt/last_error only.</summary>
    private static void RecordFailure(ProjectSync state, string error)
    {
        state.LastAttemptAt = DateTime.UtcNow;
        state.LastError = error.Length <= 512 ? error : error[..509] + "...";
        state.UpdatedAt = state.LastAttemptAt!.Value;
    }

    private static long Unix(DateTime moment) =>
        ((DateTimeOffset)DateTime.SpecifyKind(moment, DateTimeKind.Utc)).ToUnixTimeSeconds();

    private static string Sha8(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()[..8];
}
