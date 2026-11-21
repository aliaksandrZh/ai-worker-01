using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;
using Xunit;

namespace PiAgent.Infrastructure.Tests.Persistence;

/// <summary>
/// Task 10 build-pipeline model (02 §12b, decision 81): one BuildJob per AgentProfileVersion (unique),
/// buildId-before-dispatch lineage (jsonb dispatch payload), backoff bookkeeping, and the
/// validated-result columns.
/// </summary>
public class BuildJobModelTests
{
    private static AppDbContext NewDbContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=test;Username=test")
            .UseSnakeCaseNamingConvention()
            .Options);

    [Fact]
    public void BuildJob_HasSpecFields_UniquePerVersionAndJsonbPayload()
    {
        using var dbContext = NewDbContext();
        var entityType = dbContext.Model.FindEntityType(typeof(BuildJob))!;
        Assert.Equal("build_job", entityType.GetTableName());

        var present = entityType.GetProperties().Select(property => property.Name).ToHashSet();
        foreach (var name in new[]
                 {
                     "Id", nameof(BuildJob.AgentProfileVersionId), nameof(BuildJob.DispatchPayload),
                     nameof(BuildJob.State), nameof(BuildJob.Attempts), nameof(BuildJob.NextRetryAt),
                     nameof(BuildJob.ImageTag), nameof(BuildJob.ImageDigest), nameof(BuildJob.BuildLogReference),
                     nameof(BuildJob.LastError), nameof(BuildJob.CreatedAt), nameof(BuildJob.UpdatedAt),
                 })
            Assert.True(present.Contains(name), $"BuildJob.{name} missing");

        // one BuildJob per AgentProfileVersion (02 §12b); retries reuse the same buildId
        var uniqueIndexes = entityType.GetIndexes().Where(index => index.IsUnique).ToList();
        Assert.Single(uniqueIndexes, index =>
            index.Properties.Select(property => property.Name).SequenceEqual([nameof(BuildJob.AgentProfileVersionId)]));

        var payload = entityType.FindProperty(nameof(BuildJob.DispatchPayload))!;
        Assert.Equal("jsonb", payload.GetColumnType());

        var scheduleIndex = entityType.GetIndexes().SingleOrDefault(index =>
            !index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual([nameof(BuildJob.State), nameof(BuildJob.NextRetryAt)]));
        Assert.NotNull(scheduleIndex);
    }
}
