using Microsoft.Extensions.Options;
using PiAgent.Platform.Http;

namespace PiAgent.Orchestrator.Config;

public sealed class ConfigLoaderOptions
{
    /// <summary>Configuration-plane root; compose mounts it read-only at /etc/pi-agent/config.</summary>
    public string ConfigRoot { get; set; } = "config";

    /// <summary>Host-managed PAT file (owner ruling) used for GET /user and GET /repos lookups; env PI_AGENT_GITHUB_PAT_FILE.</summary>
    public string? PatFile { get; set; }

    /// <summary>Optional system-repo commit pinned as source_commit_sha traceability metadata (owner ruling, Q1).</summary>
    public string? SourceCommitSha { get; set; }

    /// <summary>
    /// Shared Runtime Bridge implementation source (single copy, `agents/runtime-bridge/` in the repo):
    /// merged into every profile's pinned build snapshot under `runtime-bridge/` (ConfigSnapshotBuilder).
    /// Env PI_AGENT_RUNTIME_BRIDGE_DIR; compose mounts it read-only at /etc/pi-agent/agents/runtime-bridge.
    /// </summary>
    public string? RuntimeBridgeSourceDir { get; set; }
}

public static class ConfigLoaderRegistration
{
    /// <summary>
    /// Registers the Config Loader as the FIRST hosted service (its StartAsync runs before the server
    /// accepts requests and before any worker tick): parse → validate → identity → one-transaction sync
    /// → gate. Any failure throws from StartAsync, failing host startup with a diagnosable error.
    /// </summary>
    public static IServiceCollection AddPiConfigLoader(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ConfigLoaderOptions>().Configure(options =>
        {
            options.ConfigRoot = configuration["PI_AGENT_CONFIG_DIR"] ?? "config";
            options.PatFile = configuration["PI_AGENT_GITHUB_PAT_FILE"];
            options.SourceCommitSha = configuration["PI_AGENT_SOURCE_COMMIT"];
            options.RuntimeBridgeSourceDir = configuration["PI_AGENT_RUNTIME_BRIDGE_DIR"] ?? "agents/runtime-bridge";
        });
        services.AddSingleton<ConfigurationState>();
        services.AddSingleton<IReadinessCheck, ConfigSyncReadinessCheck>();
        services.AddSingleton<Func<string, GitHubClient>>(sp => pat => GitHubClientFactory.Create(pat));
        services.AddSingleton<Action<int>>(_ => code => Environment.Exit(code));
        services.AddHostedService<ConfigLoaderHostedService>();
        return services;
    }
}
