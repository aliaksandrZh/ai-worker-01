using PiAgent.RuntimeApi.Provisioning;
using PiAgent.RuntimeApi.Runs;
using PiAgent.RuntimeApi.Api;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Secrets;

namespace PiAgent.RuntimeApi.Runs;

/// <summary>
/// `POST /internal/runtime/runs/{runId}/start`. The Orchestrator owns the DB rows;
/// this endpoint provisions the physical resources: the per-Run tree (plus the callback spool),
/// the per-Run callback credential + its read-only secret file, the workspace directory, and
/// the agent container over the Docker socket — mounted with the per-Run/session/log/spool
/// paths and the pinned profile version's `resource_configuration` limits. Idempotent by runId; metadata
/// written back into Run/AgentSession/ContainerInstance/Workspace.
/// </summary>
public static class StartRunHandler
{
    public sealed class Log;
    public static async Task<IResult> HandleAsync(
        Guid runId, StartRunRequest request, AppDbContext db, IContainerProvisioner provisioner, RuntimeStorageService storage,
        IOptions<ContainerProvisionerOptions> provisionerOptions, ILogger<Log> logger, CancellationToken cancellationToken)
    {
        var run = await db.Runs.SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null)
            return RunPipelineApi.Error(404, "not_found", $"run {runId} does not exist");

        var task = await db.OrchestrationTasks.SingleAsync(t => t.Id == run.TaskId, cancellationToken);
        var workflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == task.WorkflowId, cancellationToken);
        var workItem = await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == workflow.WorkItemId, cancellationToken);
        var repository = await db.Repositories.AsNoTracking().SingleAsync(r => r.Id == workItem.RepositoryId, cancellationToken);
        var session = await db.AgentSessions.SingleOrDefaultAsync(s => s.RunId == runId, cancellationToken);
        if (session is null)
            return RunPipelineApi.Error(409, "invalid_transition", $"run {runId} has no AgentSession row (Orchestrator owns it)");
        var container = await db.ContainerInstances
            .Where(c => c.RunId == runId)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (container is null)
            return RunPipelineApi.Error(409, "invalid_transition", $"run {runId} has no ContainerInstance row (Orchestrator owns it)");

        // Idempotent replay: already provisioned → return the same runtime metadata.
        if (container.ContainerName is not null)
            return Results.Json(BuildResponse(run, container, container.DockerContainerId));

        if (run.Status is not (RunStatuses.Pending or RunStatuses.Starting))
            return RunPipelineApi.Error(409, "invalid_transition", $"run {runId} is '{run.Status}', not startable");
        if (session.Status != AgentSessionStatuses.Provisioning)
            return RunPipelineApi.Error(409, "invalid_transition", $"session {session.Id} is '{session.Status}'");
        if (request.AgentSessionId != session.Id)
            return RunPipelineApi.Error(400, "validation", $"agentSessionId {request.AgentSessionId} does not match run's session {session.Id}");
        if (request.AgentProfileVersionId != task.AgentProfileVersionId)
            return RunPipelineApi.Error(400, "validation",
                $"agentProfileVersionId {request.AgentProfileVersionId} does not match run's pinned version {task.AgentProfileVersionId}");
        // `input` is the frozen context package — the RunContextSnapshot content the
        // Orchestrator assembled; the model contract is loose (a JSON object) because the example is
        // conceptual and the Bridge consumes the frozen bytes from `/input`, not this field.
        if (request.Input.ValueKind is not JsonValueKind.Object)
            return RunPipelineApi.Error(400, "validation", "input (the frozen RunContextSnapshot content) must be a JSON object");

        var snapshot = await db.RunContextSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(s => s.RunId == runId, cancellationToken);
        if (snapshot is null)
            return RunPipelineApi.Error(400, "validation", "no RunContextSnapshot exists for this run (the Orchestrator freezes it before dispatch)");

        var workspace = await db.Workspaces.SingleOrDefaultAsync(w => w.TaskId == task.Id, cancellationToken);
        if (workspace is null)
            return RunPipelineApi.Error(400, "validation", "task has no Workspace row (the Orchestrator creates it on the first attempt)");
        if (request.WorkspaceId is { } requestedWorkspace && requestedWorkspace != workspace.Id)
            return RunPipelineApi.Error(400, "validation", $"workspaceId {requestedWorkspace} does not match task workspace {workspace.Id}");

        var version = await db.AgentProfileVersions.AsNoTracking().SingleAsync(v => v.Id == task.AgentProfileVersionId, cancellationToken);
        if (string.IsNullOrEmpty(version.ContainerImage))
            return RunPipelineApi.Error(400, "validation", $"profile version {version.Id} has no built image (state '{version.State}')");

        try
        {
            // 1. per-Run tree + per-Run credential (persisted hashed; the raw token goes only to the
            //    read-only secret file the mount serves)
            var token = await RunCredentialService.IssueAsync(db, runId, cancellationToken);
            var runTree = await storage.EnsureRunTreeAsync(workflow.Id, runId, token, cancellationToken);
            var runTreeHost = storage.RunTreeHostPath(workflow.Id, runId);

            // 2. workspace directory (task workspaces persist across retries)
            storage.EnsureWorkspaceDirectory(workflow.Id, task.Id);
            var workspaceHost = storage.WorkspaceHostPath(workflow.Id, task.Id);

            // 3. container plan: pinned image, resource_configuration limits, mounts.
            // The work item's repository name travels as GIT_CLONE_REPO (not secret — profiles whose
            // entry script clones use their mounted token file; the stub ignores it).
            if (!TryBuildContainerPlan(run, task, version, storage, provisionerOptions.Value, workspaceHost, runTreeHost,
                    repository.FullName, workItem.Number, workspace.BranchName, out var plan, out var planError))
                return RunPipelineApi.Error(400, "validation", planError!);

            // 4. Docker: create + start + wait running; a created-but-not-confirmed container
            //    is removed best-effort on every failure path (a leftover with no ContainerInstance row is a leak)
            var containerId = await provisioner.CreateContainerAsync(plan!, cancellationToken);
            if (containerId is null)
                return RunPipelineApi.Error(502, "docker_failure", $"docker did not create container {plan!.Name}");
            var started = await provisioner.StartAndWaitRunningAsync(plan!.Name, TimeSpan.FromSeconds(90), cancellationToken);
            if (!started)
            {
                try { await provisioner.RemoveAsync(plan.Name, cancellationToken); }
                catch (DockerProvisioningException removalFailure)
                {
                    logger.LogWarning(removalFailure, "run {runId}: failed container {name} could not be removed",
                        runId, plan.Name);
                }
                return RunPipelineApi.Error(502, "docker_failure", $"container {plan.Name} did not reach running state");
            }

            // 5. metadata write-back — the Run transition is CONDITIONAL: a fail-fast stub/container can
            // deliver its terminal callback while provisioning is still in progress here (observed live);
            // `starting` must never clobber a terminal Run state.
            var now = DateTime.UtcNow;
            container.DockerContainerId = containerId;
            container.ContainerName = plan.Name;
            container.HostId = provisionerOptions.Value.HostId;
            container.ObservedState = ContainerObservedStates.Running;
            container.LastStartedAt = now;
            session.RuntimeSessionId = runId.ToString(); // --session-id <runId>
            session.SessionFilePath = $"{runTree.SessionDir}/{runId}.jsonl";
            session.Status = AgentSessionStatuses.InUse;
            workspace.StorageReference = workspaceHost;
            workspace.Status = WorkspaceStatuses.InUse;
            workspace.LastUsedAt = now;
            await db.Runs
                // never downgrade a Run that already went `running` (an event arrived mid-provisioning)
                .Where(r => r.Id == runId && (r.Status == RunStatuses.Pending || r.Status == RunStatuses.Starting) && r.Status != RunStatuses.Running)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(r => r.Status, RunStatuses.Starting)
                    .SetProperty(r => r.StartedAt, now), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "run {runId} started: container {container} image {image} sessionPath {sessionPath}",
                runId, plan.Name, version.ContainerImage, session.SessionFilePath);
            return Results.Json(BuildResponse(run, container, containerId));
        }
        catch (JsonException exception)
        {
            return RunPipelineApi.Error(400, "validation", $"profile version configuration is invalid JSON: {exception.Message}");
        }
        catch (IOException exception)
        {
            logger.LogError(exception, "storage provisioning failed for run {runId}", runId);
            return RunPipelineApi.Error(502, "storage_failure", $"storage provisioning failed: {exception.Message}");
        }
        catch (DockerProvisioningException exception)
        {
            logger.LogError(exception, "docker provisioning failed for run {runId}", runId);
            return RunPipelineApi.Error(502, "docker_failure", exception.Message);
        }
    }

    private static StartRunResponse BuildResponse(Run run, ContainerInstance container, string? containerId) =>
        new(
            run.Id,
            run.Id.ToString(),
            container.ContainerName ?? string.Empty,
            container.Id,
            // the Run row is updated via a conditional ExecuteUpdate; a fresh provisioning is always 'starting'
            RunStatuses.Starting,
            containerId,
            container.ContainerName);

    public static bool TryBuildContainerPlan(
        Run run,
        OrchestrationTask task,
        AgentProfileVersion version,
        RuntimeStorageService storage,
        ContainerProvisionerOptions provisionerOptions,
        string workspaceHost,
        string runTreeHost,
        string? workItemRepository,
        long? reviewPullRequestNumber,
        string? gitCloneBranch,
        out ContainerPlan? plan,
        out string? error)
    {
        plan = null;
        error = null;

        decimal? cpus = null;
        long? memoryBytes = null;
        if (version.ResourceConfiguration is { Length: > 0 } resourceConfig)
        {
            using var document = JsonDocument.Parse(resourceConfig);
            if (document.RootElement.TryGetProperty("cpu", out var cpu) && cpu.ValueKind == JsonValueKind.Number)
                cpus = cpu.GetDecimal();
            if (document.RootElement.TryGetProperty("memory", out var memory) && memory.ValueKind == JsonValueKind.String)
            {
                memoryBytes = ToDockerMemoryBytes(memory.GetString());
                if (memoryBytes is null)
                {
                    error = $"resource_configuration.memory '{memory.GetString()}' is not a supported size (e.g. 512MiB, 2GiB)";
                    return false;
                }
            }
        }

        var volumes = new List<string>
        {
            $"{workspaceHost}:/workspace",
            $"{runTreeHost}/pi-session:/session",
            $"{runTreeHost}/logs:/logs",
            $"{runTreeHost}/callback-spool:/spool",
            $"{runTreeHost}/callback-token:/run/secrets/callback-token:ro",
        };

        var versionSecrets = ParseSecretReferences(version.EnvironmentConfiguration, out var secretsError);
        if (versionSecrets is null)
        {
            error = $"environment_configuration is invalid: {secretsError}";
            return false;
        }
        foreach (var secret in versionSecrets)
        {
            var (hostPath, secretPathError) = storage.SecretHostPath(secret);
            if (hostPath is null)
            {
                // fail-closed: a missing/empty secret file mounts broken — the agent would fail
                // later with no hint; this error names the real problem at provisioning time.
                error = $"profile secret '{secret}' {secretPathError}";
                return false;
            }
            volumes.Add($"{hostPath}:/run/secrets/{secret}:ro");
        }

        var environment = new StringBuilder();
        void Env(string key, string value) { if (value.Length > 0) { if (environment.Length > 0) environment.Append('\n'); environment.Append(key).Append('=').Append(value); } }
        Env("PI_RUN_ID", run.Id.ToString());
        Env("PI_CALLBACK_BASE_URL", provisionerOptions.CallbackBaseUrl);
        Env("PI_CALLBACK_TOKEN_FILE", "/run/secrets/callback-token");
        Env("PI_HEARTBEAT_SECONDS", ParseBridgeSeconds(version.BridgeConfiguration, "heartbeat_interval", defaultValue: 15).ToString(CultureInfo.InvariantCulture));
        Env("PI_EVENT_CADENCE_SECONDS", ParseBridgeSeconds(version.BridgeConfiguration, "event_cadence", defaultValue: 5).ToString(CultureInfo.InvariantCulture));
        Env("PI_MAX_LOG_MB", storage.Options.MaxLogMegabytes.ToString(CultureInfo.InvariantCulture));
        Env("PI_SESSION_DIR", "/session");
        Env("PI_WORKSPACE", "/workspace");
        Env("STUB_BEHAVIOR", ParseBridgeBehavior(version.BridgeConfiguration));
        if (!string.IsNullOrEmpty(workItemRepository))
            Env("GIT_CLONE_REPO", workItemRepository);
        // a fix-round coder's workspace IS the pull request branch: the entry clones it directly
        // (GIT_CLONE_BRANCH) so the agent never assembles the checkout from the default branch
        if (!string.IsNullOrEmpty(gitCloneBranch))
            Env("GIT_CLONE_BRANCH", gitCloneBranch);
        // a review Task's workspace is formed from the PINNED revisions — the
        // reviewer entry checks out `target_head_sha` and forms the diff locally from `target_base_sha`.
        if (task.Type == OrchestrationTaskTypes.Review)
        {
            Env("REVIEW_HEAD_SHA", task.TargetHeadSha ?? string.Empty);
            Env("REVIEW_BASE_SHA", task.TargetBaseSha ?? string.Empty);
            if (reviewPullRequestNumber is { } prNumber)
                Env("REVIEW_PR_NUMBER", prNumber.ToString(CultureInfo.InvariantCulture));
        }

        plan = new ContainerPlan(
            Name: $"pi-session-{run.Id}",
            Image: version.ContainerImage!,
            Env: environment.Length > 0 ? environment.ToString().Split('\n') : [],
            Binds: [.. volumes],
            Labels: new Dictionary<string, string> { ["pi-run"] = run.Id.ToString(), ["pi-task"] = task.Id.ToString() },
            Cpus: cpus,
            MemoryBytes: memoryBytes,
            Network: provisionerOptions.AgentNetwork);
        return true;
    }

    /// <summary>Profile secrets as `/run/secrets/<name>` references; empty list = no mounts.
    /// The row is written by the Config Loader, so a wrong-shaped document means a corrupted jsonb row —
    /// fail closed instead of silently running the agent without its secrets.</summary>
    public static List<string>? ParseSecretReferences(string? environmentConfiguration, out string? error)
    {
        error = null;
        if (string.IsNullOrEmpty(environmentConfiguration))
            return [];
        using var document = JsonDocument.Parse(environmentConfiguration);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            error = "environment_configuration must be a JSON object with an optional 'secret_references' name array";
            return null;
        }
        if (!document.RootElement.TryGetProperty("secret_references", out var references))
            return [];
        if (references.ValueKind is not JsonValueKind.Array and not JsonValueKind.Null)
        {
            error = $"environment_configuration 'secret_references' must be an array of names (got {references.ValueKind})";
            return null;
        }
        if (references.ValueKind == JsonValueKind.Null) // profiles without a `secrets:` section sync as null
            return [];
        var names = new List<string>();
        foreach (var reference in references.EnumerateArray())
        {
            var name = reference.GetString();
            var nameError = ProfileSecretName.Validate(name);
            if (nameError is not null)
            {
                error = $"profile secret reference invalid: {nameError}";
                return null;
            }
            names.Add(name!);
        }
        return names.Distinct().ToList();
    }

    private static int ParseBridgeSeconds(string? bridgeConfiguration, string property, int defaultValue)
    {
        if (string.IsNullOrEmpty(bridgeConfiguration))
            return defaultValue;
        using var document = JsonDocument.Parse(bridgeConfiguration);
        return document.RootElement.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            && TimeSpan.TryParse(value.GetString(), CultureInfo.InvariantCulture, out var parsed)
            && parsed > TimeSpan.Zero
            ? (int)parsed.TotalSeconds
            : defaultValue;
    }

    private static string ParseBridgeBehavior(string? bridgeConfiguration)
    {
        if (string.IsNullOrEmpty(bridgeConfiguration))
            return "succeed";
        using var document = JsonDocument.Parse(bridgeConfiguration);
        return document.RootElement.TryGetProperty("behavior", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : "succeed";
    }

    /// <summary>Profile memory size → bytes for docker's `Memory` limit (GiB/MiB like `2GiB`/`512MiB`,
    /// and the same suffixes docker's binary parser accepts, `2Gb`/`512Mb`); null = unsupported.</summary>
    public static long? ToDockerMemoryBytes(string? memory)
    {
        if (memory is null)
            return null;
        var value = memory.Trim().ToLowerInvariant();
        if (value.Length > 3 && value.EndsWith("gib") && uint.TryParse(value[..^3], NumberStyles.None, CultureInfo.InvariantCulture, out var gib))
            return gib * 1024L * 1024 * 1024;
        if (value.Length > 3 && value.EndsWith("mib") && uint.TryParse(value[..^3], NumberStyles.None, CultureInfo.InvariantCulture, out var mib))
            return mib * 1024L * 1024;
        if (value.Length > 2 && value.EndsWith("gb") && uint.TryParse(value[..^2], NumberStyles.None, CultureInfo.InvariantCulture, out var gb))
            return gb * 1024L * 1024 * 1024;
        if (value.Length > 2 && value.EndsWith("mb") && uint.TryParse(value[..^2], NumberStyles.None, CultureInfo.InvariantCulture, out var mb))
            return mb * 1024L * 1024;
        return null;
    }
}
