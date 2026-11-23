using PiAgent.Platform.Http;

namespace PiAgent.Orchestrator.Config;

/// <summary>
/// Cold-start gate: until configuration synchronization completes successfully the
/// Orchestrator accepts no task-producing commands; /status, /help and health endpoints stay available.
/// The synchronized `github.bot_login` and the execution cap (`max_concurrent_runs`) travel
/// with the gate state — startup-only sync, no hot reload (comment processor + job-queue runtime).
/// </summary>
public sealed class ConfigurationState
{
    /// <summary>The global execution cap from system.yaml (`max_concurrent_runs`); default matches the parsed-config default.</summary>
    public int MaxConcurrentRuns { get; private set; } = 10;

    public bool Synchronized { get; private set; }
    public DateTime? SynchronizedAtUtc { get; private set; }
    /// <summary>Exact-case bot login from system.yaml (`github.bot_login`); set when synchronization succeeds.</summary>
    public string? BotLogin { get; private set; }
    /// <summary>First failure message (diagnosable configuration error); also surfaced via /readyz.</summary>
    public string? Error { get; private set; }

    public void MarkSynchronized(string botLogin, int maxConcurrentRuns)
    {
        Synchronized = true;
        SynchronizedAtUtc = DateTime.UtcNow;
        BotLogin = botLogin;
        MaxConcurrentRuns = maxConcurrentRuns;
        Error = null;
    }

    public void MarkFailed(string error)
    {
        if (Synchronized)
            return; // gate is sticky once synchronized (hot reload is out of scope)
        Error = error;
    }
}

public sealed class ConfigSyncReadinessCheck(ConfigurationState state) : IReadinessCheck
{
    public string Name => "config-sync";

    public Task<ReadinessResult> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(
        state.Synchronized
            ? ReadinessResult.Ready()
            : ReadinessResult.NotReady(state.Error ?? "configuration not synchronized"));
}
