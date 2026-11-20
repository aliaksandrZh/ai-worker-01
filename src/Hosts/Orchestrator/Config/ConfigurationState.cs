using PiAgent.Platform.Http;

namespace PiAgent.Orchestrator.Config;

/// <summary>
/// Cold-start gate (01 §13): until configuration synchronization completes successfully the
/// Orchestrator accepts no task-producing commands; /status, /help and health endpoints stay available.
/// Nothing consumes commands until ticket 06 — the gate state is what that admission logic will read.
/// </summary>
public sealed class ConfigurationState
{
    public bool Synchronized { get; private set; }
    public DateTime? SynchronizedAtUtc { get; private set; }
    /// <summary>First failure message (diagnosable configuration error); also surfaced via /readyz.</summary>
    public string? Error { get; private set; }

    public void MarkSynchronized()
    {
        Synchronized = true;
        SynchronizedAtUtc = DateTime.UtcNow;
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
