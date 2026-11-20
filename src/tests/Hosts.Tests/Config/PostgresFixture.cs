using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using Xunit;

namespace PiAgent.Hosts.Tests.Config;

/// <summary>
/// Real postgres 17 for configuration-plane DB tests; migrations applied (not EnsureCreated) so the
/// AddConfigurationPlane migration is exercised. Tests share the container and reset tables between
/// cases (docker is required to run the Hosts.Tests suite).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17")
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        await using var dbContext = NewDbContext();
        await dbContext.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public AppDbContext NewDbContext() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options);

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var dbContext = NewDbContext();
        await dbContext.Database.ExecuteSqlRawAsync(
            "TRUNCATE agent_profile_routing_rule, agent_profile_version, agent_profile, sync_cursor, outbox_action, repository CASCADE",
            cancellationToken);
    }
}
