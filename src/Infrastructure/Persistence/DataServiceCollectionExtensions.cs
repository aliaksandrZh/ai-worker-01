using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Http;

namespace PiAgent.Infrastructure;

public static class DataServiceCollectionExtensions
{
    /// <summary>
    /// Registers AppDbContext + pooled NpgsqlDataSource on ConnectionStrings:DefaultDb,
    /// plus the shared "postgres" readiness probe (SELECT 1).
    /// </summary>
    public static IServiceCollection AddPiData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultDb")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultDb is not configured");

        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());
        services.AddNpgsqlDataSource(connectionString);
        services.AddSingleton<IReadinessCheck, DbReadinessCheck>();
        return services;
    }
}

/// <summary>§13 health check: PostgreSQL reachable. Errors surface as readiness failures, never thrown.</summary>
public sealed class DbReadinessCheck(NpgsqlDataSource dataSource) : IReadinessCheck
{
    public string Name => "postgres";

    public async Task<ReadinessResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(cancellationToken);
            return ReadinessResult.Ready();
        }
        catch (OperationCanceledException)
        {
            return ReadinessResult.NotReady("check cancelled");
        }
        catch (Exception exception)
        {
            // Npgsql error messages carry server/role hints, never the connection string itself.
            return ReadinessResult.NotReady(exception.Message);
        }
    }
}
