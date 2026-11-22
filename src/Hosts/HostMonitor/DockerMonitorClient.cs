using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Platform.Http;

namespace PiAgent.HostMonitor;

/// <summary>One observed agent container (label `pi-run`, set by the Runtime API's provisioning).</summary>
public sealed record ObservedContainer(
    string ContainerId, string Name, Guid? RunId, Guid? TaskId, string State,
    double? CpuPercent, int? ProcessCount, string[] Processes, string[] Binds, bool Busy, string? Note);

/// <summary>
/// The local Docker Engine surface the Execution Host Monitor observes (01 §2/decision 62): container
/// list by label (`pi-run`), per-container process tree (`/top`), CPU usage (`/stats?stream=false`),
/// state and bind mounts (`/json`) — raw HTTP over the read-only unix socket, the same posture as the
/// Platform readiness check and the Build Worker's engine client.
/// </summary>
public interface IDockerMonitor
{
    Task<List<ObservedContainer>> ObserveAsync(CancellationToken cancellationToken);
}

public sealed class DockerMonitorClient(IOptions<DockerOptions> options, ILogger<DockerMonitorClient> logger) : IDockerMonitor
{
    private readonly HttpClient _http = SocketHttpClient.Create(options);

    public async Task<List<ObservedContainer>> ObserveAsync(CancellationToken cancellationToken)
    {
        var containers = new List<ObservedContainer>();
        var response = await _http.GetAsync(
            "http://localhost/containers/json?filters=" + Uri.EscapeDataString("""{"label":["pi-run"]}"""),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"docker containers/json answered {response.StatusCode:D}");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var id = element.GetProperty("Id").GetString() ?? "";
            var names = element.TryGetProperty("Names", out var namesElement) && namesElement.ValueKind == JsonValueKind.Array
                ? namesElement.EnumerateArray().Select(n => n.GetString() ?? "").ToArray()
                : [];
            var labels = element.TryGetProperty("Labels", out var labelsElement) && labelsElement.ValueKind == JsonValueKind.Object
                ? labelsElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "")
                : new Dictionary<string, string>();
            var state = element.TryGetProperty("State", out var stateElement) && stateElement.ValueKind == JsonValueKind.String
                ? stateElement.GetString() ?? "unknown"
                : "unknown";

            Guid? runId = labels.TryGetValue("pi-run", out var runLabel) && Guid.TryParse(runLabel, out var run) ? run : null;
            Guid? taskId = labels.TryGetValue("pi-task", out var taskLabel) && Guid.TryParse(taskLabel, out var task) ? task : null;

            var (processes, cpu, binds, note) = await ObserveDetailsAsync(id, cancellationToken);
            containers.Add(new ObservedContainer(
                id, names.FirstOrDefault() ?? id, runId, taskId, state,
                cpu, processes.Length, processes, binds,
                Busy: BusyProcessTree(processes, cpu),
                Note: note));
        }
        return containers;
    }

    /// <summary>Per-container details: process list (`/top`), CPU percent (one `/stats` sample) and the
    /// bind mounts (`/json`'s HostConfig.Binds). Details are best-effort: a container exiting
    /// mid-probe yields a note, never an observation failure.</summary>
    private async Task<(string[] Processes, double? Cpu, string[] Binds, string? Note)> ObserveDetailsAsync(
        string containerId, CancellationToken cancellationToken)
    {
        string[] processes = [];
        string[] binds = [];
        double? cpu = null;
        string? note = null;

        try
        {
            var topResponse = await _http.GetAsync($"http://localhost/containers/{containerId}/top", cancellationToken);
            if (topResponse.IsSuccessStatusCode)
            {
                using var top = JsonDocument.Parse(await topResponse.Content.ReadAsStringAsync(cancellationToken));
                var titles = top.RootElement.TryGetProperty("Titles", out var titlesElement)
                    ? titlesElement.EnumerateArray().Select(t => t.GetString() ?? "").ToArray()
                    : [];
                var commandIndex = FindColumn(titles, "COMMAND");
                processes = top.RootElement.TryGetProperty("Processes", out var rowsElement)
                    ? rowsElement.EnumerateArray()
                        .Where(row => row.ValueKind == JsonValueKind.Array)
                        .Select(row => commandIndex >= 0 && row.GetArrayLength() > commandIndex
                            ? row[commandIndex].GetString() ?? ""
                            : "")
                        .Where(command => command.Length > 0)
                        .ToArray()
                    : [];
            }
        }
        catch (Exception exception)
        {
            note = $"process probe failed: {exception.Message}";
        }

        try
        {
            var statsResponse = await _http.GetAsync($"http://localhost/containers/{containerId}/stats?stream=false", cancellationToken);
            if (statsResponse.IsSuccessStatusCode)
            {
                using var stats = JsonDocument.Parse(await statsResponse.Content.ReadAsStringAsync(cancellationToken));
                cpu = CpuPercent(stats.RootElement);
            }
        }
        catch (Exception exception)
        {
            note = note is null ? $"stats probe failed: {exception.Message}" : $"{note}; stats failed: {exception.Message}";
        }

        try
        {
            var inspectResponse = await _http.GetAsync($"http://localhost/containers/{containerId}/json", cancellationToken);
            if (inspectResponse.IsSuccessStatusCode)
            {
                using var inspect = JsonDocument.Parse(await inspectResponse.Content.ReadAsStringAsync(cancellationToken));
                if (inspect.RootElement.TryGetProperty("HostConfig", out var hostConfig)
                    && hostConfig.TryGetProperty("Binds", out var bindsElement)
                    && bindsElement.ValueKind == JsonValueKind.Array)
                    binds = bindsElement.EnumerateArray()
                        .Select(b => b.GetString() ?? "")
                        .Where(b => b.Length > 0)
                        .ToArray();
            }
        }
        catch (Exception exception)
        {
            note = note is null ? $"inspect probe failed: {exception.Message}" : $"{note}; inspect failed: {exception.Message}";
        }

        logger.LogDebug("container {id} observed: {processes} processes, cpu {cpu}", containerId, processes.Length, cpu);
        return (processes, cpu, binds, note);
    }

    /// <summary>Busy = an active child process tree beyond the entrypoint/supervisor trio, or notable
    /// CPU (decision 68: a long-running build/test is not killed for silence — a conservative detector).</summary>
    internal static bool BusyProcessTree(IEnumerable<string> processes, double? cpuPercent) =>
        processes.Any(p => !IsSupervisorProcess(p)) || (cpuPercent ?? 0) >= CpuBusyPercent;

    internal const double CpuBusyPercent = 2.0;

    /// <summary>The container's baseline trio is docker-init + the bridge + one agent process; any
    /// additional process (a build, a test, a compiler) makes the run busy.</summary>
    internal static bool IsSupervisorProcess(string command)
    {
        var name = command.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? command;
        return name is "docker-init" or "sleep" or "sh" or "bash" || name.EndsWith("bridge.mjs", StringComparison.Ordinal);
    }

    private static int FindColumn(string[] titles, string name)
    {
        for (var i = 0; i < titles.Length; i++)
            if (titles[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>cpu_percent from one docker stats sample (the arithmetic `docker stats` uses).</summary>
    internal static double? CpuPercent(JsonElement stats)
    {
        try
        {
            var cpu = stats.GetProperty("cpu_stats");
            var precpu = stats.GetProperty("precpu_stats");
            var delta = cpu.GetProperty("cpu_usage").GetProperty("total_usage").GetDouble()
                - precpu.GetProperty("cpu_usage").GetProperty("total_usage").GetDouble();
            var systemDelta = cpu.GetProperty("system_cpu_usage").GetDouble()
                - precpu.GetProperty("system_cpu_usage").GetDouble();
            if (systemDelta <= 0)
                return null;
            var onlineCpus = cpu.TryGetProperty("online_cpus", out var online) && online.ValueKind == JsonValueKind.Number
                ? online.GetDouble()
                : 1;
            return delta / systemDelta * onlineCpus * 100.0;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }
}
