using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using PiAgent.Events;
using PiAgent.Events.Delivery;
using PiAgent.Events.Postgres;
using PiAgent.Orchestration.Consumers;
using PiAgent.Orchestration.State;
using PiAgent.Orchestration.State.Postgres;

namespace PiAgent.Orchestration;

public static class OrchestrationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Orchestrator: state (Postgres, sharing the EventLog store's
    /// transactions), the core reactor, and its consumers under the single
    /// Workers:Orchestrator gate.
    /// </summary>
    public static IServiceCollection AddOrchestrator(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OrchestrationOptions>().Bind(configuration.GetSection(OrchestrationOptions.SectionName));
        services.AddOrchestrationState(configuration);

        services.AddSingleton<Orchestrator>();

        if (configuration.GetValue<bool?>($"Workers:Orchestrator:Enabled") is not true)
            return services;

        // concrete + interface registrations: the runner resolves the concrete consumer
        services.AddSingleton<OrchestratorConsumer>();
        services.AddSingleton<RunLifecycleConsumer>();
        services.AddSingleton<WorkflowEventsConsumer>();
        services.AddSingleton<IEventConsumer>(sp => sp.GetRequiredService<OrchestratorConsumer>());
        services.AddSingleton<IEventConsumer>(sp => sp.GetRequiredService<RunLifecycleConsumer>());
        services.AddSingleton<IEventConsumer>(sp => sp.GetRequiredService<WorkflowEventsConsumer>());

        services.AddHostedService<EventConsumerRunner<OrchestratorConsumer>>();
        services.AddHostedService<EventConsumerRunner<RunLifecycleConsumer>>();
        services.AddHostedService<EventConsumerRunner<WorkflowEventsConsumer>>();
        return services;
    }

    private static void AddOrchestrationState(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultDb")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultDb is not configured");

        services.AddSingleton(new NpgsqlDataSourceBuilder(connectionString).Build());
        services.AddSingleton(sp => sp.GetRequiredService<EventQueueStore>());
        services.AddSingleton<IOrchestrationState>(sp => new PostgresOrchestrationState(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<EventQueueStore>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<PostgresOrchestrationState>()));
    }
}
