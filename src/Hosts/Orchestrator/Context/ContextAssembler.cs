using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;

namespace PiAgent.Orchestrator.Context;

/// <summary>
/// The Orchestrator-side Context Assembler: builds one Run's input —
/// the context package — within the profile's `max_context_tokens` and the hard byte limits
/// (256 KiB per artifact / 512 KiB artifacts total / 512 KiB serialized input), and books every
/// considered source element into `included_sources` / `excluded_sources` (per-element pruning reason).
/// Owner simplification: simply COLLECTS everything available — issue body,
/// all synced comments, related work-item links, prior artifacts — and consumes it as-is; the only
/// pruning is budget-driven and deterministic (`newest-first kept`: a section stops at its first
/// overflowing element and everything older is excluded with the same reason) — no significance
/// heuristics beyond the mandatory set.
///
/// Assembly per task type: task-type instructions + pinned profile identity; the user
/// command with `source_body_snapshot` (the trailing comment text is user context) as its
/// own element; the WorkItem (title / body / state / labels); all comments chronologically with author
/// and timestamp (tombstones excluded — recorded as exclusions); `relatedWorkItems`
/// from WorkItemLink rows; prior Artifacts of the Workflow (the `TaskArtifactInput`-linked
/// approved plan is mandatory and never pruned; `log_reference` rows travel by reference,
/// never as log content); repository data; coder tasks carry the current agent-branch facts
/// from the Workspace; reviewer tasks carry the pinned `target_head_sha`/`target_base_sha` for the
/// runtime diff (the diff itself is formed locally in the workspace at provisioning,
/// never via GitHub) plus submitted reviews and grouped inline review threads (newest-first; `resolved`
/// is `null` — unknown resolution counts as unresolved).
///
/// No silent truncation anywhere: if the MANDATORY context cannot fit, nothing is pruned — the Run is
/// never started and the Task fails with `context_too_large` (the caller owns that transition via
/// TaskLifecycle). The frozen JSON is measured byte-exactly while it is composed, so budgets control the
/// same bytes that get frozen; token checks go through <see cref="ContextTokenEstimator"/>.
/// </summary>
public static class ContextAssembler
{
    /// <summary>Property names are written camelCase at the call sites; default escaping, deterministic.</summary>
    private static readonly JsonSerializerOptions Json = new();

    private static readonly Encoding Utf8 = Encoding.UTF8;

    public static async Task<ContextAssemblyResult> AssembleAsync(
        AppDbContext db,
        OrchestrationTask task,
        AgentProfileVersion version,
        Workflow workflow,
        WorkItem workItem,
        Repository repository,
        AgentCommand? command,
        CancellationToken cancellationToken)
    {
        var profileName = await db.AgentProfiles.AsNoTracking()
            .Where(p => p.Id == version.AgentProfileId)
            .Select(p => p.Name)
            .SingleAsync(cancellationToken);
        var workspace = await db.Workspaces.AsNoTracking()
            .SingleOrDefaultAsync(w => w.TaskId == task.Id, cancellationToken);
        var comments = await db.GitHubComments.AsNoTracking()
            .Where(c => c.WorkItemId == workItem.Id)
            .OrderBy(c => c.CreatedAtGithub).ThenBy(c => c.GithubCommentId)
            .ToListAsync(cancellationToken);
        var reviews = await db.GitHubReviews.AsNoTracking()
            .Where(r => r.WorkItemId == workItem.Id)
            .ToListAsync(cancellationToken);
        var artifacts = await LoadArtifactsAsync(db, workflow.Id, cancellationToken);
        var artifactPurposes = await db.TaskArtifactInputs.AsNoTracking()
            .Where(i => i.TaskId == task.Id)
            .ToDictionaryAsync(i => i.ArtifactId, i => i.Purpose, cancellationToken);
        var related = await LoadRelatedWorkItemsAsync(db, workItem, repository, cancellationToken);

        var included = new List<object?>();
        var excluded = new List<object?>();
        var composer = new Composer(ContextBudgets.MainBudgetBytes(version.MaxContextTokens));

        // ---- mandatory core: measured, never pruned; the first overflow is context_too_large ----
        if (!AddMandatory(composer, "schemaVersion", 2))
            return TooLarge(composer, "schemaVersion");
        if (!AddMandatory(composer, "profile", new { name = profileName, version = version.Version }))
            return TooLarge(composer, "profile");
        if (!AddMandatory(composer, "task", new
            {
                type = task.Type,
                targetHeadSha = task.TargetHeadSha,
                targetBaseSha = task.TargetBaseSha,
            }))
            return TooLarge(composer, "task");
        if (!AddMandatory(composer, "repository", new
            {
                fullName = repository.FullName,
                defaultBranch = repository.DefaultBranch,
            }, source: new { kind = SourceKinds.Repository, reference = repository.FullName }, included))
            return TooLarge(composer, "repository");
        if (!AddMandatory(composer, "workItem", new
            {
                repository = repository.FullName,
                number = workItem.Number,
                type = workItem.Type,
                title = workItem.Title,
                body = workItem.Body ?? "",
                state = workItem.State,
                author = workItem.AuthorLogin,
                labels = ParseLabels(workItem.Labels),
                draft = workItem.Draft,
            }, source: new { kind = SourceKinds.WorkItem, reference = $"{repository.FullName}#{workItem.Number}" }, included))
            return TooLarge(composer, "workItem");
        if (!AddMandatory(composer, "instructions", ContextInstructions.For(task.Type)))
            return TooLarge(composer, "instructions");

        if (!AddMandatory(composer, "command", command?.CommandName ?? task.Type))
            return TooLarge(composer, "command");
        if (command is { } cmd)
        {
            if (!AddMandatory(composer, "commandComment", new
                {
                    author = cmd.AuthorLogin,
                    githubCommentId = cmd.GithubCommentId,
                    createdAt = comments.FirstOrDefault(c => c.GithubCommentId == cmd.GithubCommentId)?.CreatedAtGithub ?? cmd.CreatedAt,
                    text = cmd.SourceBodySnapshot,
                }, source: new { kind = SourceKinds.CommandComment, githubCommentId = cmd.GithubCommentId }, included))
                return TooLarge(composer, "commandComment");
        }

        if (task.Type == OrchestrationTaskTypes.Implement && HasAnyGitFact(workspace))
        {
            if (!AddMandatory(composer, "git", new
                {
                    branch = workspace!.BranchName,
                    baseSha = workspace.BaseSha,
                    currentSha = workspace.CurrentSha,
                }))
                return TooLarge(composer, "git");
        }

        // ---- prunable context, newest-first kept (owner simplification: nothing except budget decides) ----
        if (task.Type == OrchestrationTaskTypes.Review)
        {
            ComposeReviews(composer, included, excluded, reviews);
            ComposeThreads(composer, included, excluded, comments, reviews);
        }
        ComposeArtifacts(composer, included, excluded, artifacts, artifactPurposes);
        ComposeRelatedWorkItems(composer, included, excluded, related);
        // the conversation carries the ISSUE/PR conversation comments (kind issue_comment) — inline review
        // comments are threads, above. Tombstoned inline comments are still recorded as exclusions.
        foreach (var tombstone in comments.Where(c => c.IsDeleted && c.Kind == GitHubCommentKinds.ReviewComment))
            excluded.Add(new { kind = SourceKinds.Comment, githubCommentId = tombstone.GithubCommentId, reason = ExcludedReasons.DeletedComment });
        ComposeConversation(composer, included, excluded,
            comments.Where(c => c.Kind == GitHubCommentKinds.IssueComment).ToList(), command);

        var content = composer.Render();
        // Defensive final re-check: composition is measured byte-exactly, so this can only fire on an
        // accounting defect — never truncate silently.
        var serializedBytes = Utf8.GetByteCount(content);
        if (composer.TooLarge || serializedBytes > composer.ByteCap)
            return new ContextAssemblyResult(
                null, $"serialized input is {serializedBytes} bytes; the budget is {composer.ByteCap} "
                    + "(mandatory context cannot fit — context_too_large)");

        return new ContextAssemblyResult(
            new ContextAssembly(
                content,
                JsonSerializer.Serialize(included, Json),
                JsonSerializer.Serialize(excluded, Json)),
            TooLargeDetail: null);
    }

    private static ContextAssemblyResult TooLarge(Composer composer, string element)
    {
        var detail = $"mandatory context element '{element}' cannot fit the context budgets"
            + (composer.TooLargeDetail is { } composerDetail ? $" ({composerDetail})" : "");
        return new ContextAssemblyResult(null, detail);
    }

    /// <summary>Mandatory sections measured and committed; `false` aborts the whole assembly.</summary>
    private static bool AddMandatory(Composer composer, string key, object value, object? source = null, List<object?>? included = null)
    {
        var json = JsonSerializer.Serialize(value, Json);
        if (!composer.AddMandatory(key, json))
            return false;
        if (source is not null)
            included!.Add(source);
        return true;
    }

    // ---- reviewer context ----

    private static void ComposeReviews(Composer composer, List<object?> included, List<object?> excluded,
        IReadOnlyList<GitHubReview> reviews)
    {
        if (composer.TooLarge)
            return;
        var ordered = reviews
            .OrderByDescending(r => r.SubmittedAtGithub ?? DateTime.MinValue).ThenByDescending(r => r.GithubReviewId)
            .ToList();
        var section = composer.BeginArray("reviews");
        for (var index = 0; index < ordered.Count; index++)
        {
            var review = ordered[index];
            var element = Serialize(new
            {
                githubReviewId = review.GithubReviewId,
                author = review.AuthorLogin,
                state = review.State,
                body = review.Body,
                submittedAt = review.SubmittedAtGithub,
                commitSha = review.CommitSha,
            });
            if (section.TryAdd(element))
            {
                included.Add(new { kind = SourceKinds.Review, githubReviewId = review.GithubReviewId, state = review.State });
                continue;
            }
            // stop-at-first-overflow: this review and every older one is excluded with the same reason
            foreach (var dropped in ordered.Skip(index))
                excluded.Add(new { kind = SourceKinds.Review, githubReviewId = dropped.GithubReviewId, reason = ExcludedReasons.PrunedByLimit });
            break;
        }
        section.DetachIfEmpty();
    }

    private static void ComposeThreads(Composer composer, List<object?> included, List<object?> excluded,
        IReadOnlyList<GitHubComment> comments, IReadOnlyList<GitHubReview> reviews)
    {
        if (composer.TooLarge)
            return;
        var reviewStateByGithubId = reviews
            .GroupBy(r => r.GithubReviewId)
            .ToDictionary(g => g.Key, g => g.First().State);

        var inline = comments
            .Where(c => c.Kind == GitHubCommentKinds.ReviewComment && !c.IsDeleted)
            .OrderBy(c => c.CreatedAtGithub).ThenBy(c => c.GithubCommentId)
            .ToList();
        var byGithubId = inline
            .GroupBy(c => c.GithubCommentId)
            .ToDictionary(g => g.Key, g => g.First());

        var threads = inline
            .Select(c => RootOf(c, byGithubId))
            .Distinct()
            .Select(rootCommentId =>
            {
                var members = inline.Where(c => RootOf(c, byGithubId) == rootCommentId).ToList();
                var root = members.FirstOrDefault(m => m.InReplyToCommentId is null) ?? members[0];
                return (Root: root, Members: members, LastActivity: members.Max(m => m.UpdatedAtGithub));
            })
            .OrderByDescending(t => t.LastActivity).ThenByDescending(t => t.Root.GithubCommentId)
            .ToList();

        var section = composer.BeginArray("threads");
        for (var index = 0; index < threads.Count; index++)
        {
            var thread = threads[index];
            var root = thread.Root;
            var path = root.Path ?? thread.Members.FirstOrDefault(m => m.Path is not null)?.Path;
            var line = root.DiffPosition ?? thread.Members.FirstOrDefault(m => m.DiffPosition is not null)?.DiffPosition;
            var state = root.GithubReviewId is { } reviewId && reviewStateByGithubId.TryGetValue(reviewId, out var reviewState)
                ? reviewState
                : null;
            var element = Serialize(new
            {
                githubCommentId = root.GithubCommentId,
                resolved = (bool?)null, // resolution state is not part of the REST sync
                state,
                path,
                line,
                comments = thread.Members.Select(m => new
                {
                    githubCommentId = m.GithubCommentId,
                    author = m.AuthorLogin,
                    createdAt = m.CreatedAtGithub,
                    body = m.Body,
                }).ToList(),
            });
            if (section.TryAdd(element))
            {
                included.Add(new
                {
                    kind = SourceKinds.ReviewThread,
                    githubCommentId = root.GithubCommentId,
                    state,
                    path,
                });
                continue;
            }
            // stop-at-first-overflow: this thread and every older one is excluded with the same reason
            foreach (var dropped in threads.Skip(index))
                excluded.Add(new { kind = SourceKinds.ReviewThread, githubCommentId = dropped.Root.GithubCommentId, reason = ExcludedReasons.PrunedByLimit });
            break;
        }
        section.DetachIfEmpty();
    }

    /// <summary>Follows in_reply_to chains to the thread's root comment (cycle-guarded; unknown parents
    /// make the comment its own root rather than dropping it).</summary>
    private static long RootOf(GitHubComment comment, IReadOnlyDictionary<long, GitHubComment> byGithubId)
    {
        var seen = new HashSet<long>();
        var current = comment;
        while (current.InReplyToCommentId is { } parentId
               && seen.Add(current.GithubCommentId)
               && byGithubId.TryGetValue(parentId, out var parent)
               && parent.GithubCommentId != current.GithubCommentId)
        {
            current = parent;
        }
        return current.GithubCommentId;
    }

    // ---- artifacts (the approved plan is mandatory) ----

    private sealed record ArtifactEntry(Artifact Artifact, int TaskSequence)
    {
        public bool Mandatory { get; init; }
    }

    private static async Task<List<ArtifactEntry>> LoadArtifactsAsync(AppDbContext db, Guid workflowId, CancellationToken cancellationToken)
    {
        var sequences = await db.OrchestrationTasks.AsNoTracking()
            .Where(t => t.WorkflowId == workflowId)
            .Select(t => new { t.Id, t.Sequence })
            .ToDictionaryAsync(t => t.Id, t => t.Sequence, cancellationToken);
        var rows = await db.Artifacts.AsNoTracking()
            .Where(a => a.WorkflowId == workflowId)
            .ToListAsync(cancellationToken);
        return rows
            .Select(a => new ArtifactEntry(a, sequences.GetValueOrDefault(a.TaskId, int.MaxValue)))
            .OrderByDescending(e => e.TaskSequence).ThenByDescending(e => e.Artifact.CreatedAt)
            .ToList();
    }

    private static void ComposeArtifacts(Composer composer, List<object?> included, List<object?> excluded,
        IReadOnlyList<ArtifactEntry> artifacts, IReadOnlyDictionary<Guid, string> purposes)
    {
        if (composer.TooLarge)
            return;
        // purposes (e.g. approved_plan) mark this Task's own input artifacts — mandatory, never pruned.
        var mandatoryIds = new HashSet<Guid>(purposes.Keys);
        var ordered = artifacts
            .Select(entry => entry with { Mandatory = mandatoryIds.Contains(entry.Artifact.Id) })
            .OrderByDescending(e => e.Mandatory)
            .ThenByDescending(e => e.TaskSequence).ThenByDescending(e => e.Artifact.CreatedAt)
            .ToList();

        var section = composer.BeginArray("artifacts");
        var artifactTotal = 0L;
        for (var index = 0; index < ordered.Count; index++)
        {
            var entry = ordered[index];
            var (element, contentBytes) = ArtifactElement(entry, purposes.GetValueOrDefault(entry.Artifact.Id));

            if (contentBytes > ContextBudgets.MaxArtifactBytes)
            {
                // artifacts are validated ≤256 KiB at creation — a violation is a corrupt row
                excluded.Add(new { kind = SourceKinds.Artifact, id = entry.Artifact.Id, type = entry.Artifact.Type, reason = ExcludedReasons.ArtifactTooLarge });
                continue;
            }

            if (entry.Mandatory && artifactTotal + contentBytes > ContextBudgets.MaxArtifactTotalBytes)
            {
                // mandatory artifacts are never pruned → no Run starts
                composer.FailTooLarge(
                    $"mandatory artifact {entry.Artifact.Id} ({contentBytes} content bytes) cannot fit the {ContextBudgets.MaxArtifactTotalBytes}-byte artifact budget");
                section.DetachIfEmpty();
                return;
            }

            var totalOverflow = !entry.Mandatory && artifactTotal + contentBytes > ContextBudgets.MaxArtifactTotalBytes;
            if (totalOverflow)
            {
                // stop-at-first-overflow: this artifact and the remaining (older) ones miss the
                // 512 KiB artifact budget — every exclusion is recorded
                foreach (var dropped in ordered.Skip(index))
                    excluded.Add(new { kind = SourceKinds.Artifact, id = dropped.Artifact.Id, type = dropped.Artifact.Type, reason = ExcludedReasons.ArtifactTotalLimit });
                break;
            }
            if (!section.TryAdd(element))
            {
                if (entry.Mandatory)
                {
                    // mandatory artifacts are never pruned → no Run starts (the main budget is exhausted)
                    composer.FailTooLarge(
                        $"mandatory artifact {entry.Artifact.Id} ({contentBytes} content bytes) cannot fit the serialized-input budget");
                    section.DetachIfEmpty();
                    return;
                }
                excluded.Add(new { kind = SourceKinds.Artifact, id = entry.Artifact.Id, type = entry.Artifact.Type, reason = ExcludedReasons.PrunedByLimit });
                break;
            }

            artifactTotal += contentBytes;
            included.Add(new
            {
                kind = SourceKinds.Artifact,
                id = entry.Artifact.Id,
                type = entry.Artifact.Type,
                purpose = purposes.GetValueOrDefault(entry.Artifact.Id),
            });
        }
        section.DetachIfEmpty();
    }

    private static (string Element, long ContentBytes) ArtifactElement(ArtifactEntry entry, string? purpose)
    {
        var artifact = entry.Artifact;
        var isLogReference = artifact.Type == ArtifactTypes.LogReference;
        var element = Serialize(new
        {
            id = artifact.Id,
            taskSequence = entry.TaskSequence,
            type = artifact.Type,
            purpose,
            format = artifact.Format,
            createdAt = artifact.CreatedAt,
            content = isLogReference ? null : artifact.Content, // log content never copies into PostgreSQL
            reference = isLogReference ? artifact.Content : null,
        });
        return (element, Utf8.GetByteCount(artifact.Content ?? ""));
    }

    // ---- related WorkItems (WorkItemLink rows) ----

    private sealed record RelatedItem(string Relation, string Repository, string OtherType, long Number, string Title, string State);

    private static async Task<List<RelatedItem>> LoadRelatedWorkItemsAsync(
        AppDbContext db, WorkItem workItem, Repository repository, CancellationToken cancellationToken)
    {
        var links = await db.WorkItemLinks.AsNoTracking()
            .Where(l => l.IssueWorkItemId == workItem.Id || l.PrWorkItemId == workItem.Id)
            .OrderByDescending(l => l.CreatedAt)
            .ToListAsync(cancellationToken);
        if (links.Count == 0)
            return [];

        var otherIds = links
            .Select(l => l.IssueWorkItemId == workItem.Id ? l.PrWorkItemId : l.IssueWorkItemId)
            .Distinct().ToList();
        var others = await db.WorkItems.AsNoTracking()
            .Where(w => otherIds.Contains(w.Id))
            .ToListAsync(cancellationToken);
        var otherRepositoryIds = others.Select(w => w.RepositoryId).Distinct().ToList();
        var names = await db.Repositories.AsNoTracking()
            .Where(r => otherRepositoryIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.FullName, cancellationToken);

        var related = new List<RelatedItem>();
        foreach (var link in links)
        {
            var otherId = link.IssueWorkItemId == workItem.Id ? link.PrWorkItemId : link.IssueWorkItemId;
            var other = others.SingleOrDefault(w => w.Id == otherId);
            if (other is null)
                continue; // not synced yet; the link is invisible to this Run
            related.Add(new RelatedItem(
                link.RelationType,
                names.GetValueOrDefault(other.RepositoryId, repository.FullName),
                other.Type, other.Number, other.Title, other.State));
        }
        return related;
    }

    private static void ComposeRelatedWorkItems(Composer composer, List<object?> included, List<object?> excluded,
        IReadOnlyList<RelatedItem> related)
    {
        if (composer.TooLarge)
            return;
        var section = composer.BeginArray("relatedWorkItems");
        for (var index = 0; index < related.Count; index++)
        {
            var item = related[index];
            var element = Serialize(new
            {
                relation = item.Relation,
                otherType = item.OtherType,
                repository = item.Repository,
                number = item.Number,
                title = item.Title,
                state = item.State,
            });
            var reference = $"{item.Repository}#{item.Number}";
            if (section.TryAdd(element))
            {
                included.Add(new { kind = SourceKinds.RelatedWorkItem, reference, relation = item.Relation });
                continue;
            }
            // stop-at-first-overflow: this link and every older one is excluded with the same reason
            foreach (var dropped in related.Skip(index))
            {
                var droppedReference = $"{dropped.Repository}#{dropped.Number}";
                excluded.Add(new { kind = SourceKinds.RelatedWorkItem, reference = droppedReference, reason = ExcludedReasons.PrunedByLimit });
            }
            break;
        }
        section.DetachIfEmpty();
    }

    // ---- conversation: every synced comment of the WorkItem, chronologically ----

    private static void ComposeConversation(Composer composer, List<object?> included, List<object?> excluded,
        IReadOnlyList<GitHubComment> comments, AgentCommand? command)
    {
        if (composer.TooLarge)
            return;
        var section = composer.BeginArray("conversation");
        var live = comments.Where(c => !c.IsDeleted).ToList(); // chronological: index 0 = oldest

        // Phase 1 — decide newest-first so recent user answers and unfinished discussions survive a
        // squeeze: walking from the newest end, the first element that does not fit marks
        // the cutoff — it and everything older is excluded with the same reason. Phase 2 composes the
        // document in chronological order (the agent reads the conversation chronologically).
        var includedIds = new HashSet<long>();
        var cutOffIndex = live.Count; // nothing excluded yet
        for (var index = live.Count - 1; index >= 0; index--)
        {
            var candidate = Serialize(new
            {
                githubCommentId = live[index].GithubCommentId,
                kind = live[index].Kind,
                author = live[index].AuthorLogin,
                createdAt = live[index].CreatedAtGithub,
                body = live[index].Body,
            });
            if (!section.Preload(candidate))
            {
                cutOffIndex = index;
                break;
            }
            includedIds.Add(live[index].GithubCommentId);
        }
        section.ResetPreload();

        foreach (var comment in comments)
        {
            if (comment.IsDeleted)
            {
                // tombstones never enter a future context — recorded as exclusions
                excluded.Add(new { kind = SourceKinds.Comment, githubCommentId = comment.GithubCommentId, reason = ExcludedReasons.DeletedComment });
                continue;
            }
            if (comment.GithubCommentId is { } id && includedIds.Contains(id))
            {
                var element = Serialize(new
                {
                    githubCommentId = comment.GithubCommentId,
                    kind = comment.Kind,
                    author = comment.AuthorLogin,
                    createdAt = comment.CreatedAtGithub,
                    body = comment.Body,
                });
                section.AddAny(element);
                included.Add(new
                {
                    kind = SourceKinds.Comment,
                    githubCommentId = comment.GithubCommentId,
                    commentKind = comment.Kind,
                    commandComment = command is { } cmd && cmd.GithubCommentId == comment.GithubCommentId,
                });
                continue;
            }
            excluded.Add(new { kind = SourceKinds.Comment, githubCommentId = comment.GithubCommentId, reason = ExcludedReasons.PrunedByLimit });
        }
        section.DetachIfEmpty();
    }

    /// <summary>stop-at-first-overflow exclusions are added inline by the callers (the overflowing
    /// element plus everything older, all with the same budget reason).</summary>

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    private static bool HasAnyGitFact(Workspace? workspace) =>
        workspace is { } workspaceValue
        && (workspaceValue.BranchName is not null || workspaceValue.BaseSha is not null || workspaceValue.CurrentSha is not null);

    private static string[] ParseLabels(string? labelsJson)
    {
        if (string.IsNullOrWhiteSpace(labelsJson))
            return [];
        try
        {
            using var document = JsonDocument.Parse(labelsJson);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToArray()
                : [];
        }
        catch (JsonException)
        {
            return []; // a corrupted jsonb row must not fail the claim; labels are context metadata
        }
    }

    /// <summary>Source-element kinds recorded in included_sources/excluded_sources.</summary>
    public static class SourceKinds
    {
        public const string Repository = "repository";
        public const string WorkItem = "work_item";
        public const string CommandComment = "command_comment";
        public const string RelatedWorkItem = "related_work_item";
        public const string Comment = "comment";
        public const string Review = "review";
        public const string ReviewThread = "review_thread";
        public const string Artifact = "artifact";
    }

    /// <summary>Excluded-source reason vocabulary (recorded verbatim in run_context_snapshot.excluded_sources).</summary>
    public static class ExcludedReasons
    {
        /// <summary>Tombstoned comment swept by the Poller.</summary>
        public const string DeletedComment = "deleted_comment";
        /// <summary>Budget-pruned element (main serialized-input / max_context_tokens budget).</summary>
        public const string PrunedByLimit = "pruned_by_limit";
        /// <summary>The artifact's content exceeds the per-artifact 256 KiB cap (defensive: artifacts are validated at creation).</summary>
        public const string ArtifactTooLarge = "artifact_too_large";
        /// <summary>The artifact would exceed the 512 KiB total artifact budget.</summary>
        public const string ArtifactTotalLimit = "artifact_total_limit";
    }

    /// <summary>
    /// Byte-exact JSON composer for the frozen input: sections are appended in document order as
    /// pre-serialized JSON fragments, and every append measures the UTF-8 bytes of the exact fragment it
    /// emits — budget checks therefore control the very bytes that get frozen (no estimation drift, no
    /// silent truncation). Array sections budget-check each element before committing; a section with no
    /// committed elements is omitted from the document entirely.
    /// </summary>
    private sealed class Composer(long byteCap)
    {
        private readonly List<(string Key, string Json)> _sections = [];
        private long _bytes = 2; // opening/closing braces — first section adds no comma (kept exact)
        private string? _tooLargeDetail;

        public string? TooLargeDetail => _tooLargeDetail;
        public bool TooLarge => _tooLargeDetail is not null;
        public long ByteCap => byteCap;

        public bool AddMandatory(string key, string json)
        {
            if (TooLarge)
                return false;
            var sectionBytes = Utf8ByteCount($"\"{key}\":") + Utf8ByteCount(json)
                + (_sections.Count > 0 ? 1 : 0); // the comma before every section after the first
            var added = _bytes + sectionBytes;
            if (added > byteCap)
            {
                FailTooLarge($"section '{key}' needs {Utf8ByteCount(json)} content bytes; {added}/{byteCap} total");
                return false;
            }
            _sections.Add((key, json));
            _bytes = added;
            return true;
        }

        /// <summary>Explicit failure from a caller that manages its own budget logic (artifacts).</summary>
        public void FailTooLarge(string detail) => _tooLargeDetail ??= detail;

        public ArraySection BeginArray(string key) => new(this, key);

        public string Render()
        {
            if (_sections.Count == 0)
                return "{}";
            return "{" + string.Join(",", _sections.Select(section => $"\"{section.Key}\":{section.Json}")) + "}";
        }

        private static long Utf8ByteCount(string text) => Encoding.UTF8.GetByteCount(text);

        /// <summary>An in-progress array section: per-element budget checks before committing (the
        /// stop-at-first-overflow policy lives in the caller). `Preload`/`ResetPreload` let a caller
        /// dry-run the budget across a phase-two composition (they never mutate the section). Sections
        /// with no committed elements are omitted from the document entirely.
        /// `TryAdd` refuses when the MAIN budget would be exceeded; `AddAny` commits unconditionally.
        /// </summary>
        public sealed class ArraySection(Composer composer, string key)
        {
            private readonly List<string> _elements = [];
            private bool _detached;
            private long _preloadedBytes;
            private int _preloadedCount;

            public bool Preload(string elementJson)
            {
                var currentTotal = composer._bytes
                    + Utf8ByteCount($"\"{key}\":") + 2 // key prefix + brackets
                    + (composer._sections.Count > 0 ? 1 : 0)
                    + InnerElementsBytes() + _preloadedBytes
                    + Utf8ByteCount(elementJson)
                    + (_preloadedCount > 0 ? 1 : 0);
                if (currentTotal + 1 > composer.ByteCap)
                    return false; // +1 = the comma committing this section would add
                _preloadedBytes += Utf8ByteCount(elementJson) + 1;
                _preloadedCount++;
                return true;
            }

            /// <summary>Drops the preload counter without mutating the section (phase-two rerun follows).</summary>
            public void ResetPreload()
            {
                _preloadedBytes = 0;
                _preloadedCount = 0;
            }

            public bool TryAdd(string elementJson)
            {
                if (!Preload(elementJson))
                    return false;
                ResetPreload();
                _elements.Add(elementJson);
                return true;
            }

            /// <summary>Commits unconditionally — the caller already resolved the budget decision.</summary>
            public void AddAny(string elementJson)
            {
                _elements.Add(elementJson);
            }

            /// <summary>Commits the accumulated elements into the composer; empty sections vanish.</summary>
            public void DetachIfEmpty()
            {
                if (_detached)
                    return;
                _detached = true;
                if (_elements.Count == 0)
                    return;
                var joined = "[" + string.Join(",", _elements) + "]";
                if (!composer.AddMandatory(key, joined))
                    composer.FailTooLarge($"section '{key}' failed its commit re-check — composer accounting bug");
            }

            private long InnerElementsBytes() =>
                _elements.Count == 0
                    ? 0
                    : _elements.Sum(UTF8ByteCountWithComma);

            private static long UTF8ByteCountWithComma(string element) => Encoding.UTF8.GetByteCount(element) + 1;
        }
    }
}
