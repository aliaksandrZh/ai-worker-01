using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using PiAgent.Infrastructure.Persistence;
using Xunit;

namespace PiAgent.Infrastructure.Tests.Persistence;

/// <summary>
/// Task 02 configuration-plane model: the five config-plane entities with spec-mandated
/// uniqueness (02 §2/§8/§12/§12a/§23), snake_case names (ER diagram), and jsonb/interval columns.
/// </summary>
public class ConfigurationPlaneModelTests
{
    private static AppDbContext NewDbContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=test;Username=test")
            .UseSnakeCaseNamingConvention()
            .Options);

    private static void AssertUniqueIndex<TEntity>(AppDbContext dbContext, params string[] propertyNames)
    {
        var entityType = dbContext.Model.FindEntityType(typeof(TEntity))!;
        var matching = entityType.GetIndexes()
            .Where(i => i.IsUnique
                && i.Properties.Select(p => p.Name).OrderBy(n => n)
                    .SequenceEqual(propertyNames.OrderBy(n => n)))
            .ToList();
        Assert.Single(matching);
    }

    private static void AssertProperties<TEntity>(AppDbContext dbContext, params string[] propertyNames)
    {
        var entityType = dbContext.Model.FindEntityType(typeof(TEntity))!;
        var present = entityType.GetProperties().Select(p => p.Name).ToHashSet();
        foreach (var name in propertyNames)
            Assert.True(present.Contains(name), $"{typeof(TEntity).Name}.{name} missing");
    }

    [Fact]
    public void EntityTypesPresent_WithSnakeCaseTableNames()
    {
        using var dbContext = NewDbContext();
        var tables = dbContext.Model.GetEntityTypes()
            .Select(e => e.GetTableName()!)
            .OrderBy(n => n)
            .ToList();
        Assert.Equal(
            new[] { "agent_command", "agent_profile", "agent_profile_routing_rule", "agent_profile_version", "agent_session", "artifact", "container_instance", "github_comment", "github_event", "github_review", "orchestration_job", "outbox_action", "repository", "run", "sync_cursor", "task", "task_artifact_input", "work_item", "work_item_link", "workflow" },
            tables);
    }

    [Fact]
    public void Repository_Fields_UniqueIndexesAndJsonb()
    {
        using var dbContext = NewDbContext();
        AssertProperties<Repository>(dbContext,
            "Id", nameof(Repository.GithubRepositoryId), nameof(Repository.Owner), nameof(Repository.Name),
            nameof(Repository.FullName), nameof(Repository.DefaultBranch), nameof(Repository.IsEnabled),
            nameof(Repository.Configuration), nameof(Repository.CreatedAt), nameof(Repository.UpdatedAt));
        AssertUniqueIndex<Repository>(dbContext, nameof(Repository.GithubRepositoryId));
        AssertUniqueIndex<Repository>(dbContext, nameof(Repository.FullName));
        var configuration = dbContext.Model.FindEntityType(typeof(Repository))!
            .FindProperty(nameof(Repository.Configuration))!;
        Assert.Equal("jsonb", configuration.GetColumnType());
        var repositoryType = dbContext.Model.FindEntityType(typeof(Repository))!;
        var githubId = repositoryType.FindProperty(nameof(Repository.GithubRepositoryId))!;
        var tableId = StoreObjectIdentifier.Table(repositoryType.GetTableName()!);
        Assert.Equal("github_repository_id", githubId.GetColumnName(tableId));
    }

    [Fact]
    public void SyncCursor_Fields_UniqueRepoStream()
    {
        using var dbContext = NewDbContext();
        AssertProperties<SyncCursor>(dbContext,
            "Id", nameof(SyncCursor.RepositoryId), nameof(SyncCursor.Stream), nameof(SyncCursor.CursorValue),
            nameof(SyncCursor.LastSuccessAt), nameof(SyncCursor.LastAttemptAt), nameof(SyncCursor.LastError),
            nameof(SyncCursor.UpdatedAt));
        AssertUniqueIndex<SyncCursor>(dbContext, nameof(SyncCursor.RepositoryId), nameof(SyncCursor.Stream));
    }

    [Fact]
    public void AgentProfile_Fields_NameUnique()
    {
        using var dbContext = NewDbContext();
        AssertProperties<AgentProfile>(dbContext,
            "Id", nameof(AgentProfile.Name), nameof(AgentProfile.Description), nameof(AgentProfile.IsEnabled),
            nameof(AgentProfile.CreatedAt), nameof(AgentProfile.UpdatedAt));
        AssertUniqueIndex<AgentProfile>(dbContext, nameof(AgentProfile.Name));
    }

    [Fact]
    public void AgentProfileVersion_Fields_UniqueProfileVersion_IntervalAndJsonb()
    {
        using var dbContext = NewDbContext();
        AssertProperties<AgentProfileVersion>(dbContext,
            "Id", nameof(AgentProfileVersion.AgentProfileId), nameof(AgentProfileVersion.Version),
            nameof(AgentProfileVersion.SourceCommitSha), nameof(AgentProfileVersion.SourceDirectory),
            nameof(AgentProfileVersion.ConfigSnapshot), nameof(AgentProfileVersion.ConfigHash),
            nameof(AgentProfileVersion.RuntimeType), nameof(AgentProfileVersion.SystemPrompt),
            nameof(AgentProfileVersion.SkillsConfiguration), nameof(AgentProfileVersion.McpConfiguration),
            nameof(AgentProfileVersion.ToolsConfiguration), nameof(AgentProfileVersion.EnvironmentConfiguration),
            nameof(AgentProfileVersion.ResourceConfiguration), nameof(AgentProfileVersion.Capabilities),
            nameof(AgentProfileVersion.RetryPolicy), nameof(AgentProfileVersion.PermissionPolicy),
            nameof(AgentProfileVersion.BridgeConfiguration), nameof(AgentProfileVersion.StartupTimeout),
            nameof(AgentProfileVersion.IdleTimeout), nameof(AgentProfileVersion.MaxRunDuration),
            nameof(AgentProfileVersion.MaxContextTokens), nameof(AgentProfileVersion.State),
            nameof(AgentProfileVersion.ContainerImage), nameof(AgentProfileVersion.ImageDigest),
            nameof(AgentProfileVersion.CreatedAt));
        AssertUniqueIndex<AgentProfileVersion>(dbContext,
            nameof(AgentProfileVersion.AgentProfileId), nameof(AgentProfileVersion.Version));
        AssertUniqueIndex<AgentProfileRoutingRule>(dbContext,
            nameof(AgentProfileRoutingRule.RepositoryId), nameof(AgentProfileRoutingRule.TaskType),
            nameof(AgentProfileRoutingRule.Priority));

        var entityType = dbContext.Model.FindEntityType(typeof(AgentProfileVersion))!;
        Assert.Equal("jsonb", entityType.FindProperty(nameof(AgentProfileVersion.ConfigSnapshot))!.GetColumnType());
        Assert.Equal("interval", entityType.FindProperty(nameof(AgentProfileVersion.StartupTimeout))!.GetColumnType());
    }

    [Fact]
    public void StreamConstants_MatchDecision53EndpointFamilies()
    {
        Assert.Equal(
            new[] { "issues", "pulls", "issue_comments", "pr_reviews", "pr_review_comments", "timeline" },
            new[] { SyncCursorStreams.Issues, SyncCursorStreams.Pulls, SyncCursorStreams.IssueComments,
                SyncCursorStreams.PrReviews, SyncCursorStreams.PrReviewComments, SyncCursorStreams.Timeline });
    }
}
