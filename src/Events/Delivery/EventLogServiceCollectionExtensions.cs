using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PiAgent.Events.Postgres;

namespace PiAgent.Events.Delivery;

public static class EventLogServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Postgres event log (producer side + delivery side). Configuration:
    /// ConnectionStrings:DefaultDb + the EventLog section (docs/eventlog.md knobs).
    /// </summary>
    public static IServiceCollection AddEventLog(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EventLogOptions>()
            .Bind(configuration.GetSection(EventLogOptions.SectionName))
            .PostConfigure(o =>
            {
                o.ConnectionString = configuration.GetConnectionString("DefaultDb")
                    ?? throw new InvalidOperationException("ConnectionStrings:DefaultDb is not configured");
            });
        services.AddSingleton<EventQueueStore>();
        services.AddSingleton<IEventLog>(sp =>
        {
            var store = sp.GetRequiredService<EventQueueStore>();
            return store;
        });
        services.AddSingleton<IEventQueue>(sp => sp.GetRequiredService<EventQueueStore>());
        services.AddSingleton<IEventReader>(sp => sp.GetRequiredService<EventQueueStore>());
        return services;
    }

    /// <summary>
    /// Registers one consumer; it starts only if Workers:{Name}:Enabled — per instance,
    /// per consumer (docs/eventlog.md invariant 7).
    /// </summary>
    public static IServiceCollection AddEventConsumer<TConsumer>(this IServiceCollection services, IConfiguration configuration)
        where TConsumer : class, IEventConsumer
    {
        var consumerName = typeof(TConsumer).Name;
        if (configuration.GetValue<bool?>($"Workers:{consumerName}:Enabled") is not true)
            return services;

        services.AddSingleton<IEventConsumer, TConsumer>();
        services.AddHostedService<EventConsumerRunner<TConsumer>>();
        return services;
    }
}
