using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Events;
using PiAgent.RunWorker.Docker;

namespace PiAgent.RunWorker;

/// <summary>
/// The run worker consumer (docs/run-worker.md): consumes run_scheduled / run_stop_requested /
/// AgentCallback, provisions the container, and settles terminal events. Terminal events are
/// never lost — the Bridge spools them; the watchdog covers crashes and timeouts.
/// </summary>
public sealed class RunWorkerConsumer(
    RunRegistry registry,
    IContainerProvisioner docker,
    IEventLog log,
    IOptions<RunWorkerOptions> options,
    ILogger<RunWorkerConsumer> logger) : IEventConsumer
{
    public Type[] Consumes { get; } = [typeof(RunScheduled), typeof(RunStopRequested), typeof(AgentCallback)];

    public string Name => "RunWorker";

    public async Task HandleAsync(IEvent evt, int attempt, CancellationToken ct)
    {
        switch (evt)
        {
            case RunScheduled scheduled:
                await StartRunAsync(scheduled, ct);
                break;

            case RunStopRequested stop:
                await StopRunAsync(stop, ct);
                break;

            case AgentCallback callback:
                await HandleCallbackAsync(callback, ct);
                break;
        }
    }

    private async Task StartRunAsync(RunScheduled scheduled, CancellationToken ct)
    {
        var runOptions = options.Value;
        var runDir = Path.Combine(runOptions.WorkspaceRoot, $"run-{scheduled.RunId}");
        Directory.CreateDirectory(runDir);
        Directory.CreateDirectory(Path.Combine(runDir, "logs"));
        await File.WriteAllTextAsync(
            Path.Combine(runDir, "input.json"),
            JsonSerializer.Serialize(scheduled.Input, new JsonSerializerOptions(JsonSerializerDefaults.Web)), ct);

        var token = Guid.NewGuid().ToString("N");
        var containerName = $"pi-session-{scheduled.RunId}";
        var entry = new RunEntry(scheduled.RunId, scheduled.TaskId, scheduled.Input,
            containerName, token, DateTime.UtcNow);
        registry.Add(entry);

        var plan = new ContainerPlan(
            Name: containerName,
            Image: $"{entry.Image}:{runOptions.ImageTag}",
            Env:
            [
                $"PI_RUN_ID={scheduled.RunId}",
                $"PI_RUN_TOKEN={token}",
                $"PI_CALLBACK_BASE_URL={runOptions.CallbackBaseUrl}",
            ],
            Binds: [$"{runDir}:/run"],
            Labels: new Dictionary<string, string> { ["pi.run.id"] = scheduled.RunId.ToString() },
            Cpus: null,
            MemoryBytes: null,
            Network: runOptions.AgentNetwork);

        try
        {
            var created = await docker.CreateContainerAsync(plan, ct)
                ?? throw new DockerProvisioningException($"container {containerName} could not be created");
            if (!await docker.StartAndWaitRunningAsync(created, TimeSpan.FromSeconds(30), ct))
                throw new DockerProvisioningException($"container {containerName} did not reach running");

            await log.AppendAsync(new RunProcessing(
                scheduled.RunId, scheduled.TaskId,
                scheduled.Input.Owner, scheduled.Input.Repo, scheduled.Input.ItemNumber), ct);
            logger.LogInformation("run {run} started in {container}", scheduled.RunId, containerName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await log.AppendAsync(new RunFailed(
                scheduled.RunId, scheduled.TaskId,
                scheduled.Input.Owner, scheduled.Input.Repo, scheduled.Input.ItemNumber,
                Cause: $"provisioning failed: {ex.Message}"), ct);
        }
    }

    private async Task StopRunAsync(RunStopRequested stop, CancellationToken ct)
    {
        var entry = registry.Get(stop.RunId);
        if (entry is null)
            return; // nothing in flight

        await docker.StopAsync(entry.ContainerName, ct);
        // container removal is disabled by owner decision: the pi-session container and its
        // logs stay on the host for diagnosability (docker logs pi-session-<runId>)
        // await docker.RemoveAsync(entry.ContainerName, ct);
        registry.Remove(stop.RunId);
        await log.AppendAsync(new RunCancelled(
            stop.RunId, entry.TaskId, entry.Input.Owner, entry.Input.Repo, entry.Input.ItemNumber), ct);
        logger.LogInformation("run {run} stopped on request", stop.RunId);
    }

    private async Task HandleCallbackAsync(AgentCallback callback, CancellationToken ct)
    {
        var entry = registry.Get(callback.RunId);
        if (entry is null)
            return; // the watchdog already settled this run

        switch (callback.Route)
        {
            case "completed":
                await SettleAsync(entry, "succeeded", null, ct);
                break;
            case "failed":
                await SettleAsync(entry, "failed", callback.Body ?? "bridge reported failure", ct);
                break;
            case "cancelled":
                await SettleAsync(entry, "cancelled", null, ct);
                break;
            default:
                logger.LogDebug("run {run} callback {route} recorded (no state)", callback.RunId, callback.Route);
                break;
        }
    }

    /// <summary>Called by the watchdog when a container exits without a terminal callback.</summary>
    internal async Task SettleOnWatchdogAsync(RunEntry entry, string cause, CancellationToken ct) =>
        await SettleAsync(entry, "failed", cause, ct);

    private async Task SettleAsync(RunEntry entry, string status, string? cause, CancellationToken ct)
    {
        registry.Remove(entry.RunId);
        await docker.StopAsync(entry.ContainerName, ct);
        // container removal is disabled by owner decision: the pi-session container and its
        // logs stay on the host for diagnosability (docker logs pi-session-<runId>)
        // await docker.RemoveAsync(entry.ContainerName, ct);

        IEvent terminal = status switch
        {
            "succeeded" => new RunCompleted(
                entry.RunId, entry.TaskId, entry.Input.Owner, entry.Input.Repo, entry.Input.ItemNumber),
            "cancelled" => new RunCancelled(
                entry.RunId, entry.TaskId, entry.Input.Owner, entry.Input.Repo, entry.Input.ItemNumber),
            _ => new RunFailed(
                entry.RunId, entry.TaskId, entry.Input.Owner, entry.Input.Repo, entry.Input.ItemNumber,
                Cause: cause ?? "unknown"),
        };
        await log.AppendAsync(terminal, ct);
        logger.LogInformation("run {run} settled as {status}", entry.RunId, status);
    }
}
