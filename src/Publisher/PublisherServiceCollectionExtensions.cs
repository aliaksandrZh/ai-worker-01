using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiAgent.Events;
using PiAgent.Events.Delivery;
using PiAgent.Platform.GitHub;
using PiAgent.Platform.Secrets;
using PiAgent.Publisher.Github;
using PiAgent.Publisher.Postgres;

namespace PiAgent.Publisher;

public static class PublisherServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Publisher: the marker store, the REST write adapter (the only
    /// GitHub write credential), and the consumer under the Workers:Publisher gate.
    /// </summary>
    public static IServiceCollection AddPublisher(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PublisherOptions>().Bind(configuration.GetSection(PublisherOptions.SectionName));
        services.AddSingleton(sp => new PostgresPublishMarkerStore(
            new Npgsql.NpgsqlDataSourceBuilder(configuration.GetConnectionString("DefaultDb")
                ?? throw new InvalidOperationException("ConnectionStrings:DefaultDb is not configured")).Build(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<PostgresPublishMarkerStore>()));
        services.AddSingleton<IPublishMarkerStore>(sp => sp.GetRequiredService<PostgresPublishMarkerStore>());

        if (configuration.GetValue<bool?>($"Workers:Publisher:Enabled") is not true)
            return services;

        services.AddSingleton<IGithubWriteApi>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<PublisherOptions>>().Value;
            var pat = PatFile.Read(options.PatFile, "PI_AGENT_GITHUB_WRITE_PAT_FILE",
                "the Publisher publishes with a repo-scoped contents+write PAT (ADR-0001)");
            return new RestGithubWriteApi(GitHubHttp.Initialize(new HttpClient
            {
                BaseAddress = new Uri("https://api.github.com/"),
                Timeout = TimeSpan.FromSeconds(30),
            }, pat, "pi-agent-publisher"), pat);
        });

        services.AddSingleton<PublisherConsumer>();
        services.AddSingleton<IEventConsumer>(sp => sp.GetRequiredService<PublisherConsumer>());
        services.AddHostedService<EventConsumerRunner<PublisherConsumer>>();
        services.AddHostedService<PublisherSchemaBootstrap>();
        return services;
    }
}

/// <summary>Idempotent marker-table DDL before the consumer starts.</summary>
internal sealed class PublisherSchemaBootstrap(PostgresPublishMarkerStore markers) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => markers.EnsureSchemaAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
