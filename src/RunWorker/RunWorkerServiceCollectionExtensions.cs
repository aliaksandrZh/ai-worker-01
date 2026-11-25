using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Events;
using PiAgent.Events.Delivery;
using PiAgent.Platform.Http;
using PiAgent.RunWorker.Docker;

namespace PiAgent.RunWorker;

public static class RunWorkerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Run Worker: docker provisioning, the run registry, the consumer
    /// (Workers:RunWorker gate), the watchdog, and the Bridge egress endpoint.
    /// </summary>
    public static IServiceCollection AddRunWorker(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RunWorkerOptions>().Bind(configuration.GetSection(RunWorkerOptions.SectionName));
        services.AddDockerSocket(configuration);
        services.AddSingleton<RunRegistry>();
        services.AddSingleton<IContainerProvisioner, DockerProvisioningClient>();

        if (configuration.GetValue<bool?>($"Workers:RunWorker:Enabled") is not true)
            return services;

        services.AddSingleton<RunWorkerConsumer>();
        services.AddSingleton<IEventConsumer>(sp => sp.GetRequiredService<RunWorkerConsumer>());
        services.AddHostedService<EventConsumerRunner<RunWorkerConsumer>>();
        services.AddHostedService<RunWatchdog>();
        return services;
    }

    /// <summary>Maps the Bridge egress endpoint on the app host.</summary>
    public static WebApplication MapRunWorker(this WebApplication app)
    {
        var registry = app.Services.GetRequiredService<RunRegistry>();
        var log = app.Services.GetRequiredService<IEventLog>();
        return app.MapAgentEventsEndpoint(registry, log);
    }
}

