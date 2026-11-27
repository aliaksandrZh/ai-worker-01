using PiAgent.Events;

namespace PiAgent.RunWorker;

/// <summary>Run Worker configuration (docs/run-worker.md knobs + provisioning posture).</summary>
public sealed class RunWorkerOptions
{
    public const string SectionName = "RunWorker";

    /// <summary>Root directory for per-Run trees (workspace + session + input).</summary>
    public string WorkspaceRoot { get; set; } = "/var/lib/pi-agent/runs";

    /// <summary>
    /// ABSOLUTE HOST path of the workspace root — the bind SOURCE the host docker daemon
    /// resolves when the per-Run tree is mounted into the agent container. The worker writes
    /// the tree through WorkspaceRoot (its own container view) but binds from here; the two
    /// are the same directory when the app host runs on the host. Empty = WorkspaceRoot.
    /// </summary>
    public string WorkspaceHostRoot { get; set; } = "";

    /// <summary>
    /// The worker's own view of the host-managed secret files (fail-closed existence check
    /// at provisioning). Empty = secret mounts are not configured.
    /// </summary>
    public string SecretsDir { get; set; } = "/etc/pi-agent/secrets";

    /// <summary>
    /// ABSOLUTE HOST path of the host-managed secret files — the bind SOURCE for
    /// `/run/secrets/<name>` mounts (resolved by the host docker daemon, never read by the
    /// worker). Empty = secret mounts are not configured; a Run carrying secret names then
    /// fails closed at provisioning (secret_missing) instead of starting without them.
    /// </summary>
    public string SecretsHostDir { get; set; } = "";

    /// <summary>
    /// The stub's scenario lever (succeed | no_completion | escalate | ...), exported as
    /// STUB_BEHAVIOR so the failure/needs-human paths stay drivable through the stack. Only
    /// the stub reads it; real profiles ignore the variable.
    /// </summary>
    public string StubBehavior { get; set; } = "succeed";

    /// <summary>Compose network agent containers join.</summary>
    public string AgentNetwork { get; set; } = "pi-agent-net";

    /// <summary>The app host address as reachable from agent containers (the events endpoint).</summary>
    public string CallbackBaseUrl { get; set; } = "http://app-host:8080";

    /// <summary>Agent image tag (agent-{profile}:{tag}); the Taskfile builds :latest — idempotent overwrite, no version tags.</summary>
    public string ImageTag { get; set; } = "latest";

    /// <summary>Watchdog ceiling per Run (seconds).</summary>
    public int RunTimeoutSeconds { get; set; } = 4 * 3600;

    /// <summary>How long after a container's exit the watchdog still expects the terminal callback (seconds).</summary>
    public int StopGraceSeconds { get; set; } = 60;

    /// <summary>The uid agent containers run as — the per-Run tree's writable dirs are chowned to it.</summary>
    public long ContainerUserId { get; set; } = 1000;
}

/// <summary>In-flight Run state for the worker (recovered lazily by container labels).</summary>
public sealed record RunEntry(
    long RunId,
    long TaskId,
    long WorkflowId,
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
