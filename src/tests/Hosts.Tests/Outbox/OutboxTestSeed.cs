using PiAgent.OutboxWorker.GitHub;
using PiAgent.BuildWorker.Docker;
using PiAgent.BuildWorker.Builds;
using PiAgent.BuildWorker.Api;
using Microsoft.EntityFrameworkCore;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Infrastructure.Persistence;
using System.Text.Json;

namespace PiAgent.Hosts.Tests.Outbox;

/// <summary>Shared seeding helpers for outbox tests: one repository + outbox_action rows on real postgres.</summary>
public static class OutboxTestSeed
{
    public static async Task<Guid> RepositoryAsync(PostgresFixture postgres)
    {
        await using var db = postgres.NewDbContext();
        var repository = new Repository
        {
            Id = Guid.NewGuid(),
            GithubRepositoryId = Random.Shared.NextInt64(1, long.MaxValue - 1),
            Owner = "aliaksandrzh",
            Name = "04",
            FullName = "aliaksandrzh/04",
            DefaultBranch = "main",
            IsEnabled = true,
            Configuration = "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Repositories.Add(repository);
        await db.SaveChangesAsync();
        return repository.Id;
    }

    /// <summary>Inserts an outbox_action row directly (payload per the store contract; key literal).</summary>
    public static async Task<Guid> ActionAsync(
        PostgresFixture postgres,
        Guid repositoryId,
        long issueNumber = 42,
        string text = "body",
        int availableOffsetMinutes = 0,
        string? key = null,
        int attempts = 0,
        Guid? workItemId = null,
        string? payload = null,
        DateTime? createdAt = null)
    {
        await using var db = postgres.NewDbContext();
        var now = DateTime.UtcNow;
        var action = new OutboxAction
        {
            Id = Guid.NewGuid(),
            RepositoryId = repositoryId,
            WorkItemId = workItemId ?? Guid.NewGuid(),
            TaskId = null,
            ActionType = OutboxActionTypes.Comment,
            Payload = payload ?? JsonSerializer.Serialize(new { issue_number = issueNumber, text }),
            Status = OutboxStatuses.Pending,
            Attempts = attempts,
            AvailableAt = now.AddMinutes(availableOffsetMinutes),
            IdempotencyKey = key ?? $"seed-{Guid.NewGuid().ToString()[..8]}",
            CreatedAt = createdAt ?? now,
        };
        db.OutboxActions.Add(action);
        await db.SaveChangesAsync();
        return action.Id;
    }

    public static async Task<OutboxAction> ReloadAsync(PostgresFixture postgres, Guid actionId)
    {
        await using var db = postgres.NewDbContext();
        return await db.OutboxActions.AsNoTracking().SingleAsync(a => a.Id == actionId);
    }

    public static string PayloadFor(long issueNumber, string text) =>
        JsonSerializer.Serialize(new { issue_number = issueNumber, text });
}
