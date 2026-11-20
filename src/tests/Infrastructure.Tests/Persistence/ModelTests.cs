using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;
using Xunit;

namespace PiAgent.Infrastructure.Tests.Persistence;

public class ModelTests
{
    private static AppDbContext NewDbContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=test;Username=test")
            .Options);

    [Fact]
    public void ModelBuilds_NearEmpty_NoEntityTypes()
    {
        using var dbContext = NewDbContext();
        Assert.Empty(dbContext.Model.GetEntityTypes());
    }

    [Fact]
    public void InitialMigrationExists()
    {
        using var dbContext = NewDbContext();
        var migrationIds = dbContext.Database.GetMigrations().ToList();
        Assert.Contains(migrationIds, id => id.EndsWith("_Initial"));
    }
}
