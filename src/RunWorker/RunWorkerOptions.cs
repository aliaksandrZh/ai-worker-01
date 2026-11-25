using PiAgent.Events;

namespace PiAgent.RunWorker;

/// <summary>Run Worker configuration (docs/run-worker.md knobs + provisioning posture).</summary>
public sealed class RunWorkerOptions
{
    public const string SectionName = "RunWorker";

    /// <summary>Root directory for per-Run trees (workspace + session + input).</summary>
    public string WorkspaceRoot { get; set; } = "/var/lib/pi-agent/runs";

    /// <summary>Compose network agent containers join.</summary>
    public string AgentNetwork { get; set; } = "pi-agent-net";

    /// <summary>The app host address as reachable from agent containers (the events endpoint).</summary>
    public string CallbackBaseUrl { get; set; } = "http://app-host:8080";

    /// <summary>Agent image naming: agent-{profile}:{tag}.</summary>
    public string ImageTag { get; set; } = "v1";

    /// <summary>Watchdog ceiling per Run (seconds).</summary>
    public int RunTimeoutSeconds { get; set; } = 4 * 3600;

    /// <summary>How long a stop waits before the container is killed (seconds).</summary>
    public int StopGraceSeconds { get; set; } = 30;
}

/// <summary>In-flight Run state for the worker (recovered lazily by container labels).</summary>
public sealed record RunEntry(
    long RunId,
    long TaskId,
    RunInput Input,
    string ContainerName,
    string Token,
    DateTime StartedUtc)
{
    public string Image => $"agent-{Input.Profile}";
}

/// <summary>Thread-safe registry of in-flight Runs (the worker's own state).</summary>
public sealed class RunRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<long, RunEntry> _runs = [];

    public void Add(RunEntry entry)
    {
        lock (_lock) _runs[entry.RunId] = entry;
    }

    public RunEntry? Get(long runId)
    {
        lock (_lock) return _runs.TryGetValue(runId, out var entry) ? entry : null;
    }

    public RunEntry? ByToken(string token)
    {
        lock (_lock) return _runs.Values.FirstOrDefault(e => e.Token == token);
    }

    public RunEntry Remove(long runId)
    {
        lock (_lock)
        {
            var entry = _runs[runId];
            _runs.Remove(runId);
            return entry;
        }
    }

    public IReadOnlyList<RunEntry> All()
    {
        lock (_lock) return _runs.Values.ToList();
    }
}
