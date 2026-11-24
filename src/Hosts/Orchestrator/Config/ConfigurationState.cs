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
    /// <summary>Board triggers from projects.yaml (`auto_plan` / `auto_code`); startup-only, no hot
    /// reload — set by the Config Loader before the synchronization gate opens.</summary>
    public IReadOnlyList<ProjectBoardTrigger> ProjectBoardTriggers { get; private set; } = [];
    /// <summary>Board status configs from projects.yaml (`ai_statuses`); startup-only, no hot reload.
    /// Drives the AI Processing → In Progress and implement-success → In Review board moves.</summary>
    public IReadOnlyList<ProjectStatusConfig> ProjectStatusConfigs { get; private set; } = [];
    /// <summary>Review-loop configs from projects.yaml (`review_loop`); startup-only, no hot reload.
    /// Caps the automatic review→fix rounds per board; boards without the config use the default (3).</summary>
    public IReadOnlyList<ReviewLoopConfig> ReviewLoopConfigs { get; private set; } = [];
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

    public void SetProjectBoardTriggers(IReadOnlyList<ProjectBoardTrigger> triggers) =>
        ProjectBoardTriggers = triggers;

    public void SetProjectStatusConfigs(IReadOnlyList<ProjectStatusConfig> configs) =>
        ProjectStatusConfigs = configs;

    public void SetReviewLoopConfigs(IReadOnlyList<ReviewLoopConfig> configs) =>
        ReviewLoopConfigs = configs;

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
