using PiAgent.Platform.Contracts;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using PiAgent.Platform.Auth;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Platform.Workers;

namespace PiAgent.HostMonitor;

/// <summary>Where the Execution Host Monitor posts its telemetry (04 §4, decision 65: a dedicated
/// service credential — never a per-Run callback token, never a write PAT).</summary>
public sealed class RuntimeApiOptions
{
    public string BaseUrl { get; set; } = "http://runtime-api:8080";
    /// <summary>Presented as `X-Service-Token`; must equal the Runtime API's `ServiceTokens__HostMonitor`.</summary>
    public string ServiceToken { get; set; } = "host-monitor-dev-token";
}

/// <summary>One posted observation, kept for the §14 metrics (counts of the last cycle).</summary>
public sealed record MonitorCycleResult(int ContainersObserved, int BusyContainers, int WorkflowDirectories);

/// <summary>
/// The Execution Host Monitor's observation cycle (01 §2, decisions 62/77, ticket 13): per tick it
/// observes agent containers (label `pi-run`: state, process tree, CPU, bind mounts) through the local
/// Docker API and the managed-storage tree (top-level workflow directories with mtimes), then posts
/// one telemetry envelope to the Runtime API's `/internal/runtime/monitor/telemetry` under its own
/// service credential. The monitor NEVER decides: it reports facts; the Runtime API's sweeper and
/// orphan reconciler make every reconciliation decision from them. Transport failures only skip the
/// cycle (the WorkerLoop logs and retries; the Runtime API simply sees no fresh observation and never
/// acts unevidenced — the fail-safe posture).
/// </summary>
public sealed class HostMonitorCycle(
    IDockerMonitor docker,
    IHttpClientFactory httpClientFactory,
    IOptions<HostMonitorOptions> storageOptions,
    IOptions<RuntimeApiOptions> apiOptions,
    ILogger<HostMonitorCycle> logger) : IWorkerCycle
{
    public MonitorCycleResult? LastCycle { get; private set; }

    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        var observedAt = DateTime.UtcNow;
        var containers = await docker.ObserveAsync(cancellationToken);
        var (directories, usedBytes) = ObserveStorage(storageOptions.Value.ManagedRoot);

        var payload = new MonitorTelemetryRequest(
            ObservedAtUtc: observedAt,
            Containers: containers.Select(c => new MonitorContainerTelemetry(
                c.ContainerId, c.Name, c.RunId, c.TaskId, c.State,
                c.CpuPercent, c.ProcessCount, c.Processes, c.Binds, c.Busy, c.Note)).ToArray(),
            Storage: new MonitorStorageTelemetry(
                storageOptions.Value.ManagedRoot, FreeBytes(storageOptions.Value.ManagedRoot), usedBytes,
                directories.Select(d => new MonitorWorkflowDirTelemetry(d.Name, d.ModifiedAtUtc, d.SizeBytes)).ToArray()));

        var client = httpClientFactory.CreateClient("host-monitor");
        client.BaseAddress = new Uri(apiOptions.Value.BaseUrl);
        // decision 65: the dedicated host credential presented as X-Service-Token on every post
        client.DefaultRequestHeaders.Remove(ServiceAuthExtensions.HeaderName);
        client.DefaultRequestHeaders.Add(ServiceAuthExtensions.HeaderName, apiOptions.Value.ServiceToken);
        var response = await client.PostAsJsonAsync("/internal/runtime/monitor/telemetry", payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"telemetry post answered {response.StatusCode:D}: {body[..Math.Min(body.Length, 200)]}");
        }

        LastCycle = new MonitorCycleResult(containers.Count, containers.Count(c => c.Busy), directories.Count);
        logger.LogInformation(
            "telemetry posted: {containers} containers ({busy} busy), {dirs} workflow directories",
            containers.Count, containers.Count(c => c.Busy), directories.Count);
    }

    internal static (List<(string Name, DateTime ModifiedAtUtc, long? SizeBytes)> Directories, long UsedBytes) ObserveStorage(string root)
    {
        var directories = new List<(string, DateTime, long?)>();
        long used = 0;
        if (!Directory.Exists(root))
            return (directories, used);
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var info = new DirectoryInfo(directory);
            var size = DirectorySize(directory);
            directories.Add((info.Name, info.LastWriteTimeUtc, size));
            used += size;
        }
        return (directories, used);
    }

    internal static long DirectorySize(string path)
    {
        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                .Sum(file => { try { return new FileInfo(file).Length; } catch { return 0L; } });
        }
        catch
        {
            return 0;
        }
    }

    private static long? FreeBytes(string root)
    {
        try
        {
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            return null;
        }
    }
}

// ---- telemetry envelope: the shared Platform.Contracts copy (the Runtime API deserializes the same types) ----
