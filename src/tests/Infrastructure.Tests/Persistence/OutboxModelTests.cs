using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using PiAgent.Infrastructure.Persistence;
using Xunit;

namespace PiAgent.Infrastructure.Tests.Persistence;

/// <summary>
/// Task 03 outbox model: OutboxAction with decision-67 lease columns (ruling 1), spec-mandated
/// indexes (02 §20/§23), jsonb payload, and the Restrict FK to repository.
/// </summary>
public class OutboxModelTests
{
    private static AppDbContext NewDbContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=test;Username=test")
            .UseSnakeCaseNamingConvention()
            .Options);

    private static IEntityType OutboxEntity(AppDbContext dbContext) =>
        dbContext.Model.FindEntityType(typeof(OutboxAction))
        ?? throw new InvalidOperationException("OutboxAction is not in the model");

    [Fact]
    public void Outbox_TableName_Columns_Defaults()
    {
        using var dbContext = NewDbContext();
        var entity = OutboxEntity(dbContext);

        Assert.Equal("outbox_action", entity.GetTableName());

        var properties = entity.GetProperties().ToDictionary(p => p.Name);
        Assert.Equal(15, properties.Count);
        Assert.True(properties.ContainsKey(nameof(OutboxAction.Id)));
        Assert.True(properties.ContainsKey(nameof(OutboxAction.RepositoryId)));
        Assert.True(properties.ContainsKey(nameof(OutboxAction.WorkItemId)));
        Assert.True(properties.ContainsKey(nameof(OutboxAction.TaskId)));
        Assert.True(properties.ContainsKey(nameof(OutboxAction.ActionType)));
        Assert.True(properties.ContainsKey(nameof(OutboxAction.Payload)));
        Assert.True(properties.ContainsKey(nameof(OutboxAction.Status)));
        Assert.True(properties.ContainsKey(nameof(OutboxAction.Attempts)));
        Assert.True(properties.ContainsKey(nameof(OutboxAction.AvailableAt)));
        Assert.True(properties.ContainsKey(nameof(OutboxAction.CompletedAt)));
        Assert.True(properties.ContainsKey(nameof(OutboxAction.LastError)));
        Assert.True(properties.ContainsKey(nameof(OutboxAction.IdempotencyKey)));
        Assert.True(properties.ContainsKey(nameof(OutboxAction.LockedBy)));
        Assert.True(properties.ContainsKey(nameof(OutboxAction.LockedUntil)));
        Assert.True(properties.ContainsKey(nameof(OutboxAction.CreatedAt)));

        Assert.Equal(OutboxActionTypes.Comment, properties[nameof(OutboxAction.ActionType)].GetDefaultValue());
        Assert.Equal(OutboxStatuses.Pending, properties[nameof(OutboxAction.Status)].GetDefaultValue());
        Assert.Equal(typeof(int), properties[nameof(OutboxAction.Attempts)].ClrType);

        foreach (var nullable in new[] { nameof(OutboxAction.CompletedAt), nameof(OutboxAction.LockedUntil),
                     nameof(OutboxAction.LockedBy), nameof(OutboxAction.LastError), nameof(OutboxAction.TaskId) })
            Assert.True(properties[nullable].IsNullable, $"{nullable} must be nullable");
        foreach (var required in new[] { nameof(OutboxAction.IdempotencyKey), nameof(OutboxAction.Payload),
                     nameof(OutboxAction.WorkItemId), nameof(OutboxAction.RepositoryId) })
            Assert.False(properties[required].IsNullable, $"{required} must be required");
    }

    [Fact]
    public void Outbox_Indexes()
    {
        using var dbContext = NewDbContext();
        var entity = OutboxEntity(dbContext);

        var idempotencyKey = entity.GetIndexes().Single(i =>
            i.IsUnique && i.Properties.Select(p => p.Name).SequenceEqual([nameof(OutboxAction.IdempotencyKey)]));
        Assert.True(idempotencyKey.IsUnique);

        var statusAvailable = entity.GetIndexes().Single(i =>
            !i.IsUnique
            && i.Properties.Select(p => p.Name).SequenceEqual(
                [nameof(OutboxAction.Status), nameof(OutboxAction.AvailableAt)]));
        Assert.False(statusAvailable.IsUnique);
    }

    [Fact]
    public void Outbox_PayloadIsJsonb()
    {
        using var dbContext = NewDbContext();
        var payload = OutboxEntity(dbContext).FindProperty(nameof(OutboxAction.Payload))!;
        Assert.Equal("jsonb", payload.GetColumnType());
    }

    [Fact]
    public void Outbox_FkToRepositoryRestrict()
    {
        using var dbContext = NewDbContext();
        var foreignKey = OutboxEntity(dbContext)
            .GetForeignKeys()
            .Single(fk => fk.PrincipalEntityType.ClrType == typeof(Repository));
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
        Assert.Equal(
            new[] { nameof(OutboxAction.RepositoryId) },
            foreignKey.Properties.Select(p => p.Name));
    }
}
