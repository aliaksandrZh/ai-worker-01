using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Events;
using PiAgent.Platform.Secrets;
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
        var runDir = RunTree.Ensure(runOptions.WorkspaceRoot, scheduled.RunId, runOptions.ContainerUserId, scheduled.Input);

        // profile secrets (docs/run-worker.md invariant 5 — provisioning is internal):
        // the host-managed files bind read-only at /run/secrets/<name>; a name that does not
        // resolve to an existing file fails the Run BEFORE the container exists — the agent
        // would fail later with no hint (fail closed, the deleted Runtime API's rule).
        var secretBinds = new List<string>();
        foreach (var name in scheduled.Secrets ?? [])
        {
            var invalid = ProfileSecretName.Validate(name);
            if (invalid is not null)
            {
                await FailProvisioningAsync(scheduled, $"invalid secret reference: {invalid}", ct);
                return;
            }
            var managed = Path.Combine(runOptions.SecretsDir, name);
            if (runOptions.SecretsDir.Length > 0 && !File.Exists(managed))
            {
                await FailProvisioningAsync(scheduled,
                    $"secret_missing: profile secret '{name}' has no host-managed file under {runOptions.SecretsDir}", ct);
                return;
            }
            if (runOptions.SecretsDir.Length > 0 && new FileInfo(managed).Length == 0)
            {
                await FailProvisioningAsync(scheduled,
                    $"secret_missing: profile secret '{name}' exists but is empty; supply the host-managed secret value", ct);
                return;
            }
            secretBinds.Add($"{runOptions.SecretsHostDir}/{name}:/run/secrets/{name}:ro");
        }

        var token = Guid.NewGuid().ToString("N");
        var containerName = $"pi-session-{scheduled.RunId}";
        var entry = new RunEntry(scheduled.RunId, scheduled.TaskId, scheduled.WorkflowId, scheduled.Input,
            containerName, token, DateTime.UtcNow);

        // entry env from the run input pointers (docs/run-worker.md "Run input (pointers v2)"):
        // one input contract — the Bridge renders the prompt from the same input.json
        var env = new List<string>
        {
            $"PI_RUN_ID={scheduled.RunId}",
            // the globally unique pi session id (docs/run-session-id.md): the Bridge opens pi
            // with it and scopes terminal eventIds by it. Rows created before the field existed
            // carry null — the run id keeps them bootable on the legacy, run-id-scoped identity.
            $"PI_SESSION_ID={scheduled.Input.SessionId ?? scheduled.RunId.ToString()}",
            $"PI_RUN_TOKEN={token}",
            $"PI_CALLBACK_BASE_URL={runOptions.CallbackBaseUrl}",
            $"GIT_CLONE_REPO={scheduled.Input.Owner}/{scheduled.Input.Repo}",
            $"GIT_CLONE_BRANCH={scheduled.Input.AgentBranch ?? scheduled.Input.DefaultBranch}",
            // the default branch as its own pointer: the reviewer entry derives the review base
            // itself (merge-base origin/<default>, pinned head) — the run input has no base SHA
            $"GIT_DEFAULT_BRANCH={scheduled.Input.DefaultBranch}",
            // the stub's scenario lever (only the stub reads it; real profiles ignore it)
            $"STUB_BEHAVIOR={runOptions.StubBehavior}",
        };
        if (scheduled.Input.ReviewHeadSha is { } headSha)
            env.Add($"REVIEW_HEAD_SHA={headSha}");
        if (scheduled.Input.PrNumber is { } prNumber)
            env.Add($"REVIEW_PR_NUMBER={prNumber}");

        // bind sources resolve on the HOST daemon (Docker-in-Docker): the workspace root the
        // worker writes through its own container view has a separate host path (same directory)
        var workspaceHostRoot = runOptions.WorkspaceHostRoot.Length > 0
            ? runOptions.WorkspaceHostRoot
            : runOptions.WorkspaceRoot;

        var plan = new ContainerPlan(
            Name: containerName,
            Image: $"{entry.Image}:{runOptions.ImageTag}",
            Env: [.. env],
            Binds:
            [
                $"{workspaceHostRoot}/run-{scheduled.RunId}/{RunTree.WorkspaceDirName}:/workspace",
                $"{workspaceHostRoot}/run-{scheduled.RunId}/session:/session",
                $"{workspaceHostRoot}/run-{scheduled.RunId}/logs:/logs",
                $"{workspaceHostRoot}/run-{scheduled.RunId}/spool:/spool",
                // the run input (pointers v2): one file, the Bridge renders the prompt from it
                $"{workspaceHostRoot}/run-{scheduled.RunId}/input.json:/run/input.json:ro",
                .. secretBinds,
            ],
            Labels: new Dictionary<string, string> { ["pi.run.id"] = scheduled.RunId.ToString() },
            Cpus: null,
            MemoryBytes: null,
            Network: runOptions.AgentNetwork);

        if (!await docker.ImageExistsAsync(plan.Image, ct))
        {
            logger.LogWarning(
                "run {run} container {container}: profile image {image} missing — failing quietly (command {command}, input {@input})",
                scheduled.RunId, containerName, plan.Image, scheduled.Input.Command, scheduled.Input);
            await FailProvisioningAsync(scheduled,
                $"image missing for profile {scheduled.Input.Profile}: {plan.Image}", ct);
            return;
        }

        registry.Add(entry);
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

    /// <summary>Fail-closed provisioning: the Run fails before the container exists, with the cause.</summary>
    private async Task FailProvisioningAsync(RunScheduled scheduled, string cause, CancellationToken ct)
    {
        await log.AppendAsync(new RunFailed(
            scheduled.RunId, scheduled.TaskId,
            scheduled.Input.Owner, scheduled.Input.Repo, scheduled.Input.ItemNumber,
            Cause: cause), ct);
        logger.LogInformation("run {run} not provisioned: {cause}", scheduled.RunId, cause);
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

        // instance-scoped callback guard (docs/run-session-id.md "Instance-scoped callbacks"):
        // a callback settles this run only if it names the run's own session. Anything else —
        // a mismatched or session-less body (pre-fix spool entries, foreign instances) — is not
        // ours; skip it entirely (covers every route incl. `tool`, which would otherwise post
        // events on the item stream).
        var sessionId = ParseSessionId(callback.Body);
        if (sessionId != entry.Input.SessionId)
        {
            logger.LogWarning("run {run}: callback from session {sid} skipped (current session {cur})",
                callback.RunId, sessionId ?? "<none>", entry.Input.SessionId ?? "<none>");
            return;
        }

        switch (callback.Route)
        {
            case "completed":
            {
                // the terminal payload's git facts (branch + head commit) are the Workflow's
                // pointers: the agent branch a fix round continues, the revision a review pins
                var facts = ParseGitFacts(callback.Body);
                await SettleAsync(entry, "succeeded", null, facts.Branch, facts.CommitSha, ct);
                break;
            }
            case "failed":
                await SettleAsync(entry, "failed", callback.Body ?? "bridge reported failure", null, null, ct);
                break;
            case "cancelled":
                await SettleAsync(entry, "cancelled", null, null, null, ct);
                break;
            case "tool":
                await HandleToolCallbackAsync(callback, entry, ct);
                break;
            default:
                logger.LogDebug("run {run} callback {route} recorded (no state)", callback.RunId, callback.Route);
                break;
        }
    }

    /// <summary>
    /// The route tool adapter (docs/run-worker.md): each communication tool call becomes
    /// exactly one typed event on the root item's stream. Deterministic garbage (unknown
    /// tool name, payload shape) fails the Run with the cause — the Orchestrator's
    /// auto-retry policy schedules the fresh Run; transient failures throw so the EventLog
    /// retries and eventually dead-letters. Non-communication routes stay opaque callbacks.
    /// </summary>
    private async Task HandleToolCallbackAsync(AgentCallback callback, RunEntry entry, CancellationToken ct)
    {
        ToolCallback tool;
        try
        {
            tool = ToolEventAdapter.Parse(callback.Body);
        }
        catch (ToolCallbackException ex)
        {
            await SettleAsync(entry, "failed", ex.Message, null, null, ct);
            return;
        }

        if (ToolEventAdapter.IsSettleMarker(tool.ToolName))
        {
            // its observation settles the run via the terminal `completed` callback — no event here
            logger.LogDebug("run {run} settle marker observed (report_completion)", callback.RunId);
            return;
        }

        try
        {
            var evt = ToolEventAdapter.Translate(tool, entry);
            await log.AppendAsync(evt, ct);
            logger.LogInformation("run {run} tool {tool} → {event} on {stream}",
                callback.RunId, tool.ToolName, evt.GetType().Name, evt.StreamKey);
        }
        catch (ToolCallbackException ex)
        {
            await SettleAsync(entry, "failed", ex.Message, null, null, ct);
        }
        // anything else propagates: the EventLog's redelivery → dead-letter covers it
    }

    /// <summary>Called by the watchdog when a container exits without a terminal callback.</summary>
    internal async Task SettleOnWatchdogAsync(RunEntry entry, string cause, CancellationToken ct) =>
        await SettleAsync(entry, "failed", cause, null, null, ct);

    private async Task SettleAsync(RunEntry entry, string status, string? cause, string? branch, string? commitSha, CancellationToken ct)
    {
        registry.Remove(entry.RunId);
        await docker.StopAsync(entry.ContainerName, ct);
        // container removal is disabled by owner decision: the pi-session container and its
        // logs stay on the host for diagnosability (docker logs pi-session-<runId>)
        // await docker.RemoveAsync(entry.ContainerName, ct);

        IEvent terminal = status switch
        {
            "succeeded" => new RunCompleted(
                entry.RunId, entry.TaskId, entry.Input.Owner, entry.Input.Repo, entry.Input.ItemNumber,
                Branch: branch, CommitSha: commitSha),
            "cancelled" => new RunCancelled(
                entry.RunId, entry.TaskId, entry.Input.Owner, entry.Input.Repo, entry.Input.ItemNumber),
            _ => new RunFailed(
                entry.RunId, entry.TaskId, entry.Input.Owner, entry.Input.Repo, entry.Input.ItemNumber,
                Cause: cause ?? "unknown"),
        };
        await log.AppendAsync(terminal, ct);
        logger.LogInformation("run {run} settled as {status}", entry.RunId, status);
    }

    /// <summary>
    /// The terminal completed payload's git facts — { result: { branch, commitSha } } —
    /// parsed tolerantly: an old-shaped or fact-less body settles without pointers.
    /// </summary>
    private static (string? Branch, string? CommitSha) ParseGitFacts(string? body)
    {
        if (body is null)
            return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var result = doc.RootElement;
            if (result.TryGetProperty("result", out var nested) && nested.ValueKind == JsonValueKind.Object)
                result = nested;
            return (
                result.TryGetProperty("branch", out var branch) && branch.ValueKind == JsonValueKind.String
                    ? branch.GetString()
                    : null,
                result.TryGetProperty("commitSha", out var sha) && sha.ValueKind == JsonValueKind.String
                    ? sha.GetString()
                    : null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// The callback body's sessionId (docs/run-session-id.md) — parsed tolerantly like the git
    /// facts; the guard treats a missing one as "not ours".
    /// </summary>
    private static string? ParseSessionId(string? body)
    {
        if (body is null)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("sessionId", out var sid)
                   && sid.ValueKind == JsonValueKind.String
                ? sid.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
