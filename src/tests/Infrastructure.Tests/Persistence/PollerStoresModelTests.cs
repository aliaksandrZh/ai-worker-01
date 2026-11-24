using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using PiAgent.Infrastructure.Persistence;
using Xunit;

namespace PiAgent.Infrastructure.Tests.Persistence;

/// <summary>
/// Poller stores: WorkItem / GitHubComment / GitHubReview / GitHubEvent —
/// unique constraints and indexes, jsonb sync-metadata columns, external-ID linkage
/// without a DB FK (github_review_id on comments), tombstone columns, defaults.
/// </summary>
public class PollerStoresModelTests
{
    private static AppDbContext NewDbContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=test;Username=test")
            .UseSnakeCaseNamingConvention()
            .Options);

    private static IEntityType Entity(AppDbContext dbContext, Type type) =>
        dbContext.Model.FindEntityType(type)
        ?? throw new InvalidOperationException($"{type.Name} is not in the model");

    private static IProperty Property(IEntityType entity, string name) =>
        entity.FindProperty(name) ?? throw new InvalidOperationException($"{name} missing on {entity.DisplayName()}");

    // ---- WorkItem ----

    [Fact]
    public void WorkItem_TableName_AndColumns()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(WorkItem));
        Assert.Equal("work_item", entity.GetTableName());

        var properties = entity.GetProperties().ToDictionary(p => p.Name);
        Assert.Equal(24, properties.Count); // + head_ref, review_fix_rounds (the review→fix loop)
        foreach (var required in new[] { nameof(WorkItem.Id), nameof(WorkItem.RepositoryId), nameof(WorkItem.GithubNodeId),
                     nameof(WorkItem.Number), nameof(WorkItem.Type), nameof(WorkItem.Title), nameof(WorkItem.State),
                     nameof(WorkItem.AuthorLogin), nameof(WorkItem.Assignees), nameof(WorkItem.Labels),
                     nameof(WorkItem.CreatedAtGithub), nameof(WorkItem.UpdatedAtGithub), nameof(WorkItem.LastSyncedAt) })
            Assert.False(properties[required].IsNullable, $"{required} must be required");
        foreach (var nullable in new[] { nameof(WorkItem.Body), nameof(WorkItem.ClosedAtGithub), nameof(WorkItem.HeadSha),
                     nameof(WorkItem.BaseSha), nameof(WorkItem.Draft), nameof(WorkItem.MergedAt),
                     nameof(WorkItem.RequestedReviewers), nameof(WorkItem.CleanupStatus), nameof(WorkItem.ClosedAtLocal) })
            Assert.True(properties[nullable].IsNullable, $"{nullable} must be nullable");
    }

    [Fact]
    public void WorkItem_UniqueIndexes()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(WorkItem));
        Assert.Contains(entity.GetIndexes(), i => i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual([nameof(WorkItem.RepositoryId), nameof(WorkItem.Number)]));
        Assert.Contains(entity.GetIndexes(), i => i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual([nameof(WorkItem.GithubNodeId)]));
    }

    [Fact]
    public void WorkItem_SyncMetadata_IsJsonb_AndFkCascade()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(WorkItem));
        foreach (var jsonb in new[] { nameof(WorkItem.Assignees), nameof(WorkItem.Labels), nameof(WorkItem.RequestedReviewers) })
            Assert.Equal("jsonb", Property(entity, jsonb).GetColumnType());

        var foreignKey = entity.GetForeignKeys()
            .Single(fk => fk.PrincipalEntityType.ClrType == typeof(Repository));
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
    }

    // ---- GitHubComment ----

    [Fact]
    public void GitHubComment_TableName_AndColumns()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(GitHubComment));
        Assert.Equal("github_comment", entity.GetTableName());

        var properties = entity.GetProperties().ToDictionary(p => p.Name);
        Assert.Equal(16, properties.Count);
        Assert.Equal(false, Property(entity, nameof(GitHubComment.IsDeleted)).GetDefaultValue());
        foreach (var nullable in new[] { nameof(GitHubComment.InReplyToCommentId), nameof(GitHubComment.GithubReviewId),
                     nameof(GitHubComment.Path), nameof(GitHubComment.CommitSha), nameof(GitHubComment.DiffPosition),
                     nameof(GitHubComment.DeletedAt) })
            Assert.True(properties[nullable].IsNullable, $"{nullable} must be nullable");
        foreach (var required in new[] { nameof(GitHubComment.WorkItemId), nameof(GitHubComment.GithubCommentId),
                     nameof(GitHubComment.Kind), nameof(GitHubComment.Body), nameof(GitHubComment.CreatedAtGithub),
                     nameof(GitHubComment.UpdatedAtGithub), nameof(GitHubComment.LastSyncedAt) })
            Assert.False(properties[required].IsNullable, $"{required} must be required");
    }

    [Fact]
    public void GitHubComment_UniqueKindExternalId_And_ThreadIndex()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(GitHubComment));
        Assert.Contains(entity.GetIndexes(), i => i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual([nameof(GitHubComment.Kind), nameof(GitHubComment.GithubCommentId)]));
        Assert.Contains(entity.GetIndexes(), i => !i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual([nameof(GitHubComment.WorkItemId), nameof(GitHubComment.CreatedAtGithub)]));
    }

    [Fact]
    public void GitHubComment_FkCascade_NoDbFkForReviewId()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(GitHubComment));
        var workItemFk = entity.GetForeignKeys()
            .Single(fk => fk.PrincipalEntityType.ClrType == typeof(WorkItem));
        Assert.Equal(DeleteBehavior.Cascade, workItemFk.DeleteBehavior);
        // github_review_id is an external ID only: no FK to github_review
        Assert.DoesNotContain(entity.GetForeignKeys(), fk => fk.PrincipalEntityType.ClrType == typeof(GitHubReview));
    }

    // ---- GitHubReview ----

    [Fact]
    public void GitHubReview_TableName_UniqueExternalId_FkCascade()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(GitHubReview));
        Assert.Equal("github_review", entity.GetTableName());
        Assert.Equal(9, entity.GetProperties().Count());
        Assert.Contains(entity.GetIndexes(), i => i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual([nameof(GitHubReview.GithubReviewId)]));
        var workItemFk = entity.GetForeignKeys()
            .Single(fk => fk.PrincipalEntityType.ClrType == typeof(WorkItem));
        Assert.Equal(DeleteBehavior.Cascade, workItemFk.DeleteBehavior);
        Assert.True(Property(entity, nameof(GitHubReview.SubmittedAtGithub)).IsNullable);
    }

    // ---- GitHubEvent ----

    [Fact]
    public void GitHubEvent_TableName_AndColumns()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(GitHubEvent));
        Assert.Equal("github_event", entity.GetTableName());

        var properties = entity.GetProperties().ToDictionary(p => p.Name);
        Assert.Equal(13, properties.Count);
        Assert.Equal(GitHubEventStatuses.Pending, Property(entity, nameof(GitHubEvent.ProcessingStatus)).GetDefaultValue());
        Assert.Equal("jsonb", Property(entity, nameof(GitHubEvent.Payload)).GetColumnType());
        foreach (var nullable in new[] { nameof(GitHubEvent.WorkItemId), nameof(GitHubEvent.ExternalEventId),
                     nameof(GitHubEvent.DedupeKey), nameof(GitHubEvent.OccurredAtGithub), nameof(GitHubEvent.ProcessedAt),
                     nameof(GitHubEvent.ProcessingError) })
            Assert.True(properties[nullable].IsNullable, $"{nullable} must be nullable");
        foreach (var required in new[] { nameof(GitHubEvent.RepositoryId), nameof(GitHubEvent.Source),
                     nameof(GitHubEvent.EventType), nameof(GitHubEvent.Payload), nameof(GitHubEvent.ReceivedAt) })
            Assert.False(properties[required].IsNullable, $"{required} must be required");
    }

    [Fact]
    public void GitHubEvent_DedupeUniqueAndProcessingIndex()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(GitHubEvent));
        // (source, dedupe_key) unique "where applicable" — PostgreSQL NULLs are distinct,
        // so a plain unique index admits unkeyed events without a partial filter.
        Assert.Contains(entity.GetIndexes(), i => i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual([nameof(GitHubEvent.Source), nameof(GitHubEvent.DedupeKey)]));
        Assert.Contains(entity.GetIndexes(), i => !i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual(
                [nameof(GitHubEvent.RepositoryId), nameof(GitHubEvent.ProcessingStatus), nameof(GitHubEvent.ReceivedAt)]));
    }

    [Fact]
    public void GitHubEvent_WorkItemFkSetNull_RepositoryFkCascade()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(GitHubEvent));
        var workItemFk = entity.GetForeignKeys()
            .Single(fk => fk.PrincipalEntityType.ClrType == typeof(WorkItem));
        Assert.Equal(DeleteBehavior.SetNull, workItemFk.DeleteBehavior);
        var repositoryFk = entity.GetForeignKeys()
            .Single(fk => fk.PrincipalEntityType.ClrType == typeof(Repository));
        Assert.Equal(DeleteBehavior.Cascade, repositoryFk.DeleteBehavior);
    }

    // ---- WorkItemLink (typed role columns) ----

    [Fact]
    public void WorkItemLink_TableName_Columns_UniqueIndex()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(WorkItemLink));
        Assert.Equal("work_item_link", entity.GetTableName());

        var properties = entity.GetProperties().ToDictionary(p => p.Name);
        foreach (var required in new[] { nameof(WorkItemLink.Id), nameof(WorkItemLink.IssueWorkItemId),
                     nameof(WorkItemLink.PrWorkItemId), nameof(WorkItemLink.RelationType),
                     nameof(WorkItemLink.Source), nameof(WorkItemLink.CreatedAt) })
            Assert.False(properties[required].IsNullable, $"{required} must be required");

        // unique (issue, pr, relation) — source/target uniqueness in typed-role shape
        Assert.Contains(entity.GetIndexes(), i => i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual(
                [nameof(WorkItemLink.IssueWorkItemId), nameof(WorkItemLink.PrWorkItemId), nameof(WorkItemLink.RelationType)]));

        // both sides reference work_item with cascade (mirrors github_comment/github_review)
        Assert.Equal(2, entity.GetForeignKeys().Count());
        Assert.All(entity.GetForeignKeys(), fk =>
        {
            Assert.Equal(typeof(WorkItem), fk.PrincipalEntityType.ClrType);
            Assert.Equal(DeleteBehavior.Cascade, fk.DeleteBehavior);
        });
    }

    [Fact]
    public void WorkItemLink_Enums()
    {
        Assert.Equal("closes", WorkItemLinkRelations.Closes);
        Assert.Equal("related_to", WorkItemLinkRelations.RelatedTo);
        Assert.Equal("github_closing_reference", WorkItemLinkSources.GithubClosingReference);
        Assert.Equal("github_cross_reference", WorkItemLinkSources.GithubCrossReference);
        // raw GitHub timeline names — dispatch is by (source, event_type)
        Assert.Equal("cross-referenced", GitHubTimelineEvents.CrossReferenced);
        Assert.Equal("closed", GitHubTimelineEvents.Closed);
        Assert.Equal("connected", GitHubTimelineEvents.Connected);
        Assert.Equal("disconnected", GitHubTimelineEvents.Disconnected);
    }

    // ---- Event-type taxonomy constants ----

    [Fact]
    public void EventTypes_MatchNormalizedNaming()
    {
        Assert.Equal("work_item_created", GitHubEventTypes.WorkItemCreated);
        Assert.Equal("work_item_updated", GitHubEventTypes.WorkItemUpdated);
        Assert.Equal("work_item_closed", GitHubEventTypes.WorkItemClosed);
        Assert.Equal("issue_comment_created", GitHubEventTypes.IssueCommentCreated);
        Assert.Equal("issue_comment_updated", GitHubEventTypes.IssueCommentUpdated);
        Assert.Equal("issue_comment_deleted", GitHubEventTypes.IssueCommentDeleted);
        Assert.Equal("review_comment_created", GitHubEventTypes.ReviewCommentCreated);
        Assert.Equal("review_comment_updated", GitHubEventTypes.ReviewCommentUpdated);
        Assert.Equal("review_comment_deleted", GitHubEventTypes.ReviewCommentDeleted);
        Assert.Equal("pull_request_review_submitted", GitHubEventTypes.PullRequestReviewSubmitted);
        Assert.Equal("pull_request_review_dismissed", GitHubEventTypes.PullRequestReviewDismissed);
        Assert.Equal("review_requested", GitHubEventTypes.ReviewRequested);
        Assert.Equal("review_request_removed", GitHubEventTypes.ReviewRequestRemoved);
        Assert.Equal("pull_request_synchronized", GitHubEventTypes.PullRequestSynchronized);
        Assert.Equal("rest_resource_sync", GitHubEventSources.RestResourceSync);
        Assert.Equal("snapshot_diff", GitHubEventSources.SnapshotDiff);
        Assert.Equal("pending", GitHubEventStatuses.Pending);
        Assert.Equal("issue", WorkItemTypes.Issue);
        Assert.Equal("pull_request", WorkItemTypes.PullRequest);
        Assert.Equal("issue_comment", GitHubCommentKinds.IssueComment);
        Assert.Equal("review_comment", GitHubCommentKinds.ReviewComment);
    }
}
