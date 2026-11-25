using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiAgent.Events.Delivery;
using PiAgent.Platform.GitHub;
using PiAgent.Platform.Secrets;
using PiAgent.GithubSync.Github;

namespace PiAgent.GithubSync;

public static class GithubSyncServiceCollectionExtensions
{
    /// <summary>
    /// Registers the GitHub sync adapter: cursors, the read client (a read-only PAT),
    /// and the worker under the Workers:GithubSync gate. The sync holds no GitHub-shaped
    /// tables — cursors only (docs/github-sync.md).
    /// </summary>
    public static IServiceCollection AddGithubSync(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(sp => SyncConfig.Read(
            configuration["PI_AGENT_CONFIG_DIR"] ?? "config"));
        services.AddSingleton(sp => new PostgresCursorStore(
            new Npgsql.NpgsqlDataSourceBuilder(configuration.GetConnectionString("DefaultDb")
                ?? throw new InvalidOperationException("ConnectionStrings:DefaultDb is not configured")).Build(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<PostgresCursorStore>()));
        services.AddSingleton<ICursorStore>(sp => sp.GetRequiredService<PostgresCursorStore>());
        services.AddHostedService<CursorSchemaBootstrap>();

        if (configuration.GetValue<bool?>($"Workers:GithubSync:Enabled") is not true)
            return services;

        services.AddSingleton<IGithubReadApi>(sp =>
        {
            var pat = PatFile.Read(configuration["PI_AGENT_GITHUB_PAT_FILE"], "PI_AGENT_GITHUB_PAT_FILE",
                "the sync polls with a fine-grained read-only PAT");
            return new RestGithubReadApi(GitHubHttp.Initialize(new HttpClient
            {
                BaseAddress = new Uri("https://api.github.com/"),
                Timeout = TimeSpan.FromSeconds(30),
            }, pat, "pi-agent-sync"));
        });
        services.AddHostedService<GithubSyncWorker>();
        return services;
    }
}

/// <summary>Idempotent cursor-table DDL before the worker starts.</summary>
internal sealed class CursorSchemaBootstrap(PostgresCursorStore cursors) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => cursors.EnsureSchemaAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
