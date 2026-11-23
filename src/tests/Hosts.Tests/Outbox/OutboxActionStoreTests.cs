using PiAgent.OutboxWorker.GitHub;
using PiAgent.BuildWorker.Docker;
using PiAgent.BuildWorker.Builds;
using PiAgent.BuildWorker.Api;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Infrastructure.Persistence;
using Xunit;

namespace PiAgent.Hosts.Tests.Outbox;

/// <summary>
/// OutboxActionStore semantics over real postgres: idempotent enqueue,
/// atomic SKIP-LOCKED claim with lease + retry-window exhaustion, owner-guarded transitions,
/// cancel. All timestamps use UTC with a 5 s tolerance for clock travel between reads.
/// </summary>
public class OutboxActionStoreTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(5);

    private static async Task<Guid> SeedRepositoryAsync(AppDbContext db)
    {
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

    private static OutboxEnqueueRequest Request(Guid repositoryId, Guid? key = null, string text = "system notification body", Guid? workItemId = null) =>
        new(repositoryId, workItemId ?? Guid.NewGuid(), null, 42, text, key);

    private static async Task<Guid> EnqueueAsync(PostgresFixture postgres, Guid repositoryId, Guid? key = null, string text = "body")
    {
        await using var db = postgres.NewDbContext();
        var action = await new OutboxActionStore(db)
            .EnqueueAsync(Request(repositoryId, key: key, text: text), CancellationToken.None);
        return action.Id;
    }

    [Fact]
    public async Task Enqueue_PersistsDefaults()
    {
        await postgres.ResetAsync();
        var repositoryId = await SeedRepository(postgres);

        var id = await EnqueueAsync(postgres, repositoryId, text: "Final failure notice");

        await using var db = postgres.NewDbContext();
        var action = await db.OutboxActions.SingleAsync(a => a.Id == id);
        Assert.Equal(OutboxStatuses.Pending, action.Status);
        Assert.Equal(OutboxActionTypes.Comment, action.ActionType);
        Assert.Equal(0, action.Attempts);
        Assert.Null(action.CompletedAt);
        Assert.Null(action.LastError);
        Assert.Null(action.LockedBy);
        Assert.Null(action.LockedUntil);
        Assert.NotEmpty(action.IdempotencyKey);
        Assert.True((DateTime.UtcNow - action.AvailableAt).Duration() <= Tolerance, $"available_at {action.AvailableAt:O}");
        Assert.True((DateTime.UtcNow - action.CreatedAt).Duration() <= Tolerance);

        var payload = JsonDocument.Parse(action.Payload);
        Assert.Equal(42, payload.RootElement.GetProperty("issue_number").GetInt64());
        Assert.Equal("Final failure notice", payload.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Enqueue_DuplicateIdempotencyKey_ReturnsExistingRow()
    {
        await postgres.ResetAsync();
        var repositoryId = await SeedRepository(postgres);
        var key = Guid.NewGuid();

        var firstId = await EnqueueAsync(postgres, repositoryId, key: key);
        {
            // make the existing row clearly non-fresh: a second enqueue must not reset attempts
            await using var setup = postgres.NewDbContext();
            await setup.OutboxActions.ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Attempts, 3)
                .SetProperty(a => a.Status, OutboxStatuses.Processing));
        }

        var secondId = await EnqueueAsync(postgres, repositoryId, key: key);

        await using var db = postgres.NewDbContext();
        Assert.Equal(1, await db.OutboxActions.CountAsync());
        Assert.Equal(firstId, secondId);
        var row = await db.OutboxActions.SingleAsync();
        Assert.Equal(3, row.Attempts);
        Assert.Equal(OutboxStatuses.Processing, row.Status);
    }

    [Fact]
    public async Task Enqueue_InvalidArguments_Throw()
    {
        await postgres.ResetAsync();
        var repositoryId = await SeedRepository(postgres);
        await using var db = postgres.NewDbContext();
        var store = new OutboxActionStore(db);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.EnqueueAsync(Request(repositoryId, text: "   "), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.EnqueueAsync(Request(repositoryId) with { IssueNumber = 0 }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.EnqueueAsync(Request(Guid.NewGuid()), CancellationToken.None));
    }

    [Fact]
    public async Task ClaimNext_ClaimsDueOldestFirst_CountsAttempt_Locks()
    {
        await postgres.ResetAsync();
        var repositoryId = await SeedRepository(postgres);
        var notDueId = await SeedActionAsync(postgres, repositoryId, availableOffsetMinutes: +10);
        await SeedActionAsync(postgres, repositoryId, availableOffsetMinutes: -1, key: "b-old");
        await SeedActionAsync(postgres, repositoryId, availableOffsetMinutes: -2, key: "a-oldest");
        var dispatcherId = Guid.NewGuid();

        await using var db = postgres.NewDbContext();
        var store = new OutboxActionStore(db);

        var first = await store.ClaimNextAsync(dispatcherId, TimeSpan.FromMinutes(2), TimeSpan.FromHours(24), CancellationToken.None);
        Assert.Equal("a-oldest", await IdempotencyKeyOf(postgres, first!.Id));
        Assert.Equal(OutboxStatuses.Processing, first.Status);
        Assert.Equal(1, first.Attempts);
        Assert.Equal(dispatcherId, first.LockedBy);
        Assert.True(first.LockedUntil is { } leaseUntil
            && leaseUntil >= DateTime.UtcNow.AddMinutes(2) - Tolerance
            && leaseUntil <= DateTime.UtcNow.AddMinutes(2) + Tolerance, $"locked_until {first.LockedUntil:O}");

        var second = await store.ClaimNextAsync(Guid.NewGuid(), TimeSpan.FromMinutes(2), TimeSpan.FromHours(24), CancellationToken.None);
        Assert.Equal("b-old", await IdempotencyKeyOf(postgres, second!.Id));

        var third = await store.ClaimNextAsync(Guid.NewGuid(), TimeSpan.FromMinutes(2), TimeSpan.FromHours(24), CancellationToken.None);
        Assert.Null(third);
    }

    [Fact]
    public async Task ClaimNext_ConcurrentClaims_DoNotOverlap()
    {
        await postgres.ResetAsync();
        var repositoryId = await SeedRepository(postgres);

        var singleRowId = await SeedActionAsync(postgres, repositoryId);
        var (firstWinner, secondWinner) = await ClaimConcurrentlyAsync(postgres, dispatchers: 2);
        Assert.True(firstWinner is null ^ secondWinner is null, "exactly one concurrent claim wins on one row");
        await using var db = postgres.NewDbContext();
        Assert.Equal(1, await db.OutboxActions.Where(a => a.Id == singleRowId).SumAsync(a => a.Attempts));

        var rowA = await SeedActionAsync(postgres, repositoryId, key: "row-a");
        var rowB = await SeedActionAsync(postgres, repositoryId, key: "row-b");
        var (winnerA, winnerB) = await ClaimConcurrentlyAsync(postgres, dispatchers: 2);
        Assert.NotNull(winnerA);
        Assert.NotNull(winnerB);
        Assert.NotEqual(winnerA, winnerB);
    }

    [Fact]
    public async Task ClaimNext_ExpiredLease_Reclaims()
    {
        await postgres.ResetAsync();
        var repositoryId = await SeedRepository(postgres);
        var actionId = await SeedActionAsync(postgres, repositoryId);
        var ownerA = Guid.NewGuid();
        await using (var db = postgres.NewDbContext())
        {
            var claimed = await new OutboxActionStore(db)
                .ClaimNextAsync(ownerA, TimeSpan.FromMinutes(2), TimeSpan.FromHours(24), CancellationToken.None);
            Assert.Equal(actionId, claimed!.Id);
            await db.OutboxActions.ExecuteUpdateAsync(s => s.SetProperty(a => a.LockedUntil, DateTime.UtcNow.AddSeconds(-1)));
        }

        Guid ownerB = Guid.NewGuid();
        await using var reclaimDb = postgres.NewDbContext();
        var reclaimed = await new OutboxActionStore(reclaimDb)
            .ClaimNextAsync(ownerB, TimeSpan.FromMinutes(2), TimeSpan.FromHours(24), CancellationToken.None);

        Assert.Equal(actionId, reclaimed!.Id);
        Assert.Equal(2, reclaimed.Attempts);
        Assert.Equal(ownerB, reclaimed.LockedBy);
    }

    [Fact]
    public async Task ClaimNext_RetryWindowExhausted_DeadLetters()
    {
        await postgres.ResetAsync();
        var repositoryId = await SeedRepository(postgres);
        var actionId = await SeedActionAsync(postgres, repositoryId, attempts: 1);
        {
            await using var setup = postgres.NewDbContext();
            await setup.OutboxActions.ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Attempts, 1)
                .SetProperty(a => a.CreatedAt, DateTime.UtcNow.AddHours(-25)));
        }
        var dispatcherId = Guid.NewGuid();

        await using var db = postgres.NewDbContext();
        var claimed = await new OutboxActionStore(db)
            .ClaimNextAsync(dispatcherId, TimeSpan.FromMinutes(2), TimeSpan.FromHours(24), CancellationToken.None);

        Assert.Null(claimed);
        await using var verify = postgres.NewDbContext();
        var row = await verify.OutboxActions.SingleAsync(a => a.Id == actionId);
        Assert.Equal(OutboxStatuses.DeadLetter, row.Status);
        Assert.Contains("retry window exhausted", row.LastError);
        Assert.Equal(1, row.Attempts);
        var payload = JsonDocument.Parse(row.Payload);
        Assert.Equal(42, payload.RootElement.GetProperty("issue_number").GetInt64());
        Assert.Equal("body", payload.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task MarkCompleted_OwnerSucceeds_StaleOwnerNoOp()
    {
        await postgres.ResetAsync();
        var repositoryId = await SeedRepository(postgres);
        var owner = Guid.NewGuid();
        var actionId = await SeedActionAsync(postgres, repositoryId);
        await using (var db = postgres.NewDbContext())
        {
            var claimed = await new OutboxActionStore(db)
                .ClaimNextAsync(owner, TimeSpan.FromMinutes(2), TimeSpan.FromHours(24), CancellationToken.None);
            Assert.Equal(actionId, claimed!.Id);
        }

        await using var otherOwnerDb = postgres.NewDbContext();
        var stale = await new OutboxActionStore(otherOwnerDb)
            .MarkCompletedAsync(actionId, Guid.NewGuid(), CancellationToken.None);
        Assert.False(stale);

        await using var completeDb = postgres.NewDbContext();
        var completed = await new OutboxActionStore(completeDb)
            .MarkCompletedAsync(actionId, owner, CancellationToken.None);
        Assert.True(completed);

        await using var verify = postgres.NewDbContext();
        var row = await verify.OutboxActions.SingleAsync(a => a.Id == actionId);
        Assert.Equal(OutboxStatuses.Completed, row.Status);
        Assert.NotNull(row.CompletedAt);
        Assert.Null(row.LockedBy);
        Assert.Null(row.LockedUntil);
    }

    [Fact]
    public async Task MarkTransientFailure_SetsBackoff()
    {
        await postgres.ResetAsync();
        var repositoryId = await SeedRepository(postgres);
        var owner = Guid.NewGuid();
        await SeedActionAsync(postgres, repositoryId);
        await using var claimDb = postgres.NewDbContext();
        var claimed = await new OutboxActionStore(claimDb)
            .ClaimNextAsync(owner, TimeSpan.FromMinutes(2), TimeSpan.FromHours(24), CancellationToken.None);
        Assert.NotNull(claimed);

        await using var failDb = postgres.NewDbContext();
        await new OutboxActionStore(failDb)
            .MarkTransientFailureAsync(claimed!.Id, owner, "HTTP 502: bad gateway after retries", TimeSpan.FromSeconds(64), CancellationToken.None);

        await using var verify = postgres.NewDbContext();
        var row = await verify.OutboxActions.SingleAsync(a => a.Id == claimed.Id);
        Assert.Equal(OutboxStatuses.Pending, row.Status);
        Assert.Null(row.LockedBy);
        Assert.Null(row.LockedUntil);
        Assert.Equal("HTTP 502: bad gateway after retries", row.LastError);
        Assert.True(row.AvailableAt >= DateTime.UtcNow.AddSeconds(64) - Tolerance
            && row.AvailableAt <= DateTime.UtcNow.AddSeconds(64) + Tolerance, $"available_at {row.AvailableAt:O}");
    }

    [Fact]
    public async Task MarkDeadLetter_RetainsPayloadAttemptsError()
    {
        await postgres.ResetAsync();
        var repositoryId = await SeedRepository(postgres);
        var owner = Guid.NewGuid();
        await SeedActionAsync(postgres, repositoryId);
        await using var claimDb = postgres.NewDbContext();
        var claimed = await new OutboxActionStore(claimDb)
            .ClaimNextAsync(owner, TimeSpan.FromMinutes(2), TimeSpan.FromHours(24), CancellationToken.None);
        Assert.Equal(1, claimed!.Attempts);
        var payloadBefore = claimed.Payload;

        await using var failDb = postgres.NewDbContext();
        await new OutboxActionStore(failDb)
            .MarkDeadLetterAsync(claimed.Id, owner, "HTTP 401: bad credentials", CancellationToken.None);

        await using var verify = postgres.NewDbContext();
        var row = await verify.OutboxActions.SingleAsync(a => a.Id == claimed.Id);
        Assert.Equal(OutboxStatuses.DeadLetter, row.Status);
        Assert.Equal(payloadBefore, row.Payload);
        Assert.Equal(1, row.Attempts);
        Assert.Equal("HTTP 401: bad credentials", row.LastError);
        Assert.Null(row.CompletedAt);
        Assert.Null(row.LockedBy);
    }

    [Fact]
    public async Task CancelPendingForWorkItem_OnlyPending()
    {
        await postgres.ResetAsync();
        var repositoryId = await SeedRepository(postgres);
        var workItemId = Guid.NewGuid();
        await SeedActionAsync(postgres, repositoryId, workItemId: workItemId, key: "c-1");
        await SeedActionAsync(postgres, repositoryId, workItemId: workItemId, key: "c-2");
        await SeedActionAsync(postgres, repositoryId, workItemId: workItemId, key: "c-3");
        await using (var db = postgres.NewDbContext())
        {
            var owner = Guid.NewGuid();
            var claimed = await new OutboxActionStore(db)
                .ClaimNextAsync(owner, TimeSpan.FromMinutes(2), TimeSpan.FromHours(24), CancellationToken.None);
            Assert.Equal("c-1", await IdempotencyKeyOf(postgres, claimed!.Id));
            await new OutboxActionStore(db).MarkCompletedAsync(claimed.Id, owner, CancellationToken.None);
        }

        await using var cancelDb = postgres.NewDbContext();
        var cancelled = await new OutboxActionStore(cancelDb)
            .CancelPendingForWorkItemAsync(workItemId, CancellationToken.None);

        Assert.Equal(2, cancelled);
        await using var verify = postgres.NewDbContext();
        Assert.Equal(2, await verify.OutboxActions.CountAsync(a => a.WorkItemId == workItemId && a.Status == OutboxStatuses.Cancelled));
        Assert.Equal(1, await verify.OutboxActions.CountAsync(a => a.Status == OutboxStatuses.Completed));
    }

    // --- helpers -------------------------------------------------------

    private static Task<Guid> SeedRepository(PostgresFixture fixture) =>
        SeedRepositoryAsync(fixture.NewDbContext());

    private static string PayloadFor(long issueNumber = 42, string text = "body") =>
        JsonSerializer.Serialize(new { issue_number = issueNumber, text });

    private static async Task<Guid> SeedActionAsync(
        PostgresFixture postgres,
        Guid repositoryId,
        int availableOffsetMinutes = 0,
        string? key = null,
        int attempts = 0,
        Guid? workItemId = null)
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
            Payload = PayloadFor(),
            Status = OutboxStatuses.Pending,
            Attempts = attempts,
            AvailableAt = now.AddMinutes(availableOffsetMinutes),
            IdempotencyKey = key?.ToString() ?? $"seed-{Guid.NewGuid().ToString()[..8]}",
            CreatedAt = now,
        };
        db.OutboxActions.Add(action);
        await db.SaveChangesAsync();
        return action.Id;
    }

    private static async Task<(Guid? First, Guid? Second)> ClaimConcurrentlyAsync(PostgresFixture postgres, int dispatchers)
    {
        var claims = await Task.WhenAll(Enumerable.Range(0, dispatchers).Select(_ => Task.Run(async () =>
        {
            await using var db = postgres.NewDbContext();
            var claimed = await new OutboxActionStore(db)
                .ClaimNextAsync(Guid.NewGuid(), TimeSpan.FromMinutes(2), TimeSpan.FromHours(24), CancellationToken.None);
            return claimed?.Id;
        })));
        return (claims.ElementAtOrDefault(0), claims.ElementAtOrDefault(1));
    }

    private static async Task<string> IdempotencyKeyOf(PostgresFixture postgres, Guid id)
    {
        await using var db = postgres.NewDbContext();
        return (await db.OutboxActions.AsNoTracking().SingleAsync(a => a.Id == id)).IdempotencyKey;
    }
}
