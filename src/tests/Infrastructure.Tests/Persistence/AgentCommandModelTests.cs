using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using PiAgent.Infrastructure.Persistence;
using Xunit;

namespace PiAgent.Infrastructure.Tests.Persistence;

/// <summary>
/// agent_command metadata per 02 §11/§23 (task 06): unique GitHub comment ID (the command idempotency
/// key), (status, created_at) query index, jsonb arguments, Restrict FK on work_item, canonical
/// name/status literals.
/// </summary>
public class AgentCommandModelTests
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

    [Fact]
    public void AgentCommand_TableName_AndColumns()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(AgentCommand));
        Assert.Equal("agent_command", entity.GetTableName());

        var properties = entity.GetProperties().ToDictionary(p => p.Name);
        Assert.Equal(13, properties.Count);
        foreach (var required in new[] { nameof(AgentCommand.Id), nameof(AgentCommand.WorkItemId),
                     nameof(AgentCommand.GithubCommentId), nameof(AgentCommand.Arguments),
                     nameof(AgentCommand.ParserVersion), nameof(AgentCommand.Status),
                     nameof(AgentCommand.SourceBodySnapshot), nameof(AgentCommand.CreatedAt) })
            Assert.False(properties[required].IsNullable, $"{required} must be required");
        foreach (var nullable in new[] { nameof(AgentCommand.AuthorLogin), nameof(AgentCommand.CommandName),
                     nameof(AgentCommand.ParsedAt), nameof(AgentCommand.ProcessedAt), nameof(AgentCommand.RejectionReason) })
            Assert.True(properties[nullable].IsNullable, $"{nullable} must be nullable");
    }

    [Fact]
    public void AgentCommand_GithubCommentId_IsUnique()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(AgentCommand));
        Assert.Contains(entity.GetIndexes(), i => i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual([nameof(AgentCommand.GithubCommentId)]));
    }

    [Fact]
    public void AgentCommand_StatusCreatedAtIndex()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(AgentCommand));
        Assert.Contains(entity.GetIndexes(), i => !i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual([nameof(AgentCommand.Status), nameof(AgentCommand.CreatedAt)]));
    }

    [Fact]
    public void AgentCommand_ArgumentsIsJsonb_AndWorkItemFkRestrict()
    {
        using var dbContext = NewDbContext();
        var entity = Entity(dbContext, typeof(AgentCommand));
        Assert.Equal("jsonb", Property(entity, nameof(AgentCommand.Arguments)).GetColumnType());

        var foreignKey = entity.GetForeignKeys()
            .Single(fk => fk.PrincipalEntityType.ClrType == typeof(WorkItem));
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
    }
}
