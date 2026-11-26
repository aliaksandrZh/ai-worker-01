using System.Text.RegularExpressions;
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
using PiAgent.Platform.Config;

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
        services.AddOptions<OrchestrationOptions>()
            .Bind(configuration.GetSection(OrchestrationOptions.SectionName))
            .PostConfigure(o =>
            {
                if (o.DefaultBranches.Count == 0)
                    o.DefaultBranches = RepositoriesConfig.ReadDefaultBranches(
                        configuration["PI_AGENT_CONFIG_DIR"] ?? "config");
                if (o.ProfileSecrets.Count == 0)
                    o.ProfileSecrets = ReadProfileSecrets(
                        configuration["PI_AGENT_CONFIG_DIR"] ?? "config");
            });
        services.AddOrchestrationState(configuration);

        services.AddSingleton<Orchestrator>();

        if (configuration.GetValue<bool?>($"Workers:Orchestrator:Enabled") is not true)
            return services;

        // concrete + interface registrations: the runner resolves the concrete consumer
        services.AddSingleton<OrchestratorInbound>();
        services.AddSingleton<OrchestratorRuns>();
        services.AddSingleton<OrchestratorAgent>();
        services.AddSingleton<IEventConsumer>(sp => sp.GetRequiredService<OrchestratorInbound>());
        services.AddSingleton<IEventConsumer>(sp => sp.GetRequiredService<OrchestratorRuns>());
        services.AddSingleton<IEventConsumer>(sp => sp.GetRequiredService<OrchestratorAgent>());

        services.AddHostedService<EventConsumerRunner<OrchestratorInbound>>();
        services.AddHostedService<EventConsumerRunner<OrchestratorRuns>>();
        services.AddHostedService<EventConsumerRunner<OrchestratorAgent>>();
        return services;
    }

    /// <summary>
    /// Profile secret NAMES from the configuration plane's system.json (`profile_secrets:` —
    /// the frozen agents/&lt;name&gt;/profile.yaml `secrets:` lists): names only, values never
    /// enter configuration. An absent key = a profile without secret mounts; a malformed name
    /// fails startup (the config plane is the schema authority). The name grammar mirrors
    /// Platform's ProfileSecretName (the worker's runtime validator).
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadProfileSecrets(string configDir)
    {
        // the Orchestrator reads a NARROW subset of system.json (github:, knobs… belong to
        // other modules) — unknown properties are ignored by the shared reader
        var root = ConfigJson.Read<SystemJsonRoot>(Path.Combine(configDir, "system.json"));
        if (root is null)
            return new Dictionary<string, IReadOnlyList<string>>();

        var secrets = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var (profile, names) in root.ProfileSecrets)
        {
            foreach (var name in names)
            {
                if (!ValidSecretName(name))
                    throw new InvalidOperationException(
                        $"configuration error: profile secret '{name}' of profile '{profile}': " +
                        "secret reference must match [A-Za-z0-9][A-Za-z0-9._-]* (a single file name)");
            }
            secrets[profile] = names;
        }
        return secrets;
    }

    /// <summary>The narrow system.json subset the Orchestrator reads (profile_secrets).</summary>
    private sealed class SystemJsonRoot
    {
        public Dictionary<string, List<string>> ProfileSecrets { get; init; } = [];
    }

    private static readonly Regex SecretNamePattern = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);

    private static bool ValidSecretName(string name) =>
        !string.IsNullOrWhiteSpace(name) && SecretNamePattern.IsMatch(name);

    private static void AddOrchestrationState(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultDb")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultDb is not configured");

        services.AddSingleton(new NpgsqlDataSourceBuilder(connectionString).Build());
        services.AddSingleton(sp => new PostgresOrchestrationState(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<EventQueueStore>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<PostgresOrchestrationState>()));
        services.AddSingleton<IOrchestrationState>(sp => sp.GetRequiredService<PostgresOrchestrationState>());
    }
}
