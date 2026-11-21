using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PiAgent.Infrastructure.Persistence;

namespace PiAgent.RuntimeApi;

/// <summary>
/// The four callback routes (04 §4): every delivery is persisted as a RunEvent (unique `(run_id, event_id)`)
/// <em>before</em> acknowledgement; duplicates are idempotent no-ops. Terminal routes set the Run's terminal
/// state, record the log trio facts + a `log_reference` Artifact, revoke the per-Run credential, stop the
/// container, and release the workspace; `completed` also creates the validated manifest's Artifact rows
/// (+ an `artifact_produced` event per artifact). Validation failures persist nothing (the Bridge answers
/// them by posting the terminal `failed` callback with the diagnostics — no silent truncation).
/// </summary>
public static class CallbackHandler
{
    /// <summary>The persisted payload mirrors the delivered body verbatim in camelCase (consumer contract:
    /// the Orchestrator's terminal consumer reads e.g. `outcome` from the completed payload).</summary>
    internal static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    private const long MaxArtifactBytes = 256 * 1024; // §16 max_artifact_size, hard limit
    private static readonly string[] ManifestArtifactTypes =
        [ArtifactTypes.Plan, ArtifactTypes.TestCases, ArtifactTypes.ReviewReport, ArtifactTypes.ImplementationSummary];

    public static async Task<IResult> HeartbeatAsync(Guid runId, HeartbeatRequest request, AppDbContext db, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.EventId))
            return RunPipelineApi.Error(400, "validation", "eventId is required");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await PersistEventAsync(db, runId, request.EventId, RunEventTypes.Heartbeat,
                JsonSerializer.Serialize(new { request.EventId, request.OccurredAt }),
                request.OccurredAt, cancellationToken) is { } duplicate)
        {
            await transaction.RollbackAsync(cancellationToken);
            return duplicate; // 200: idempotent replay of an already-persisted beat (02 §18)
        }
        var now = DateTime.UtcNow;
        await db.Runs.Where(r => r.Id == runId)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.LastHeartbeatAt, now), cancellationToken);
        await db.AgentSessions.Where(s => s.RunId == runId)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.LastHeartbeatAt, now), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Results.Json(new { persisted = true });
    }

    public static async Task<IResult> EventAsync(Guid runId, EventRequest request, AppDbContext db, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.EventId))
            return RunPipelineApi.Error(400, "validation", "eventId is required");
        if (!RunEventTypes.All.Contains(request.Type))
            return RunPipelineApi.Error(400, "unknown_event_type", $"type '{request.Type}' is not part of the RunEvent set (02 §18)");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var eventPayload = request.Payload is { } value ? JsonSerializer.Serialize(value) : null;
        if (await PersistEventAsync(db, runId, request.EventId, request.Type, eventPayload, request.OccurredAt, cancellationToken) is { } duplicate)
        {
            await transaction.RollbackAsync(cancellationToken);
            return duplicate;
        }
        if (RunEventTypes.Progress.Contains(request.Type))
        {
            var now = DateTime.UtcNow;
            await db.Runs.Where(r => r.Id == runId)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.LastProgressAt, now), cancellationToken);
            await db.AgentSessions.Where(s => s.RunId == runId)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.LastActivityAt, now), cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return Results.Json(new { persisted = true });
    }

    public static async Task<IResult> CompletedAsync(
        Guid runId, CompletedRequest request, AppDbContext db, RuntimeStorageService storage, IKomodoClient komodo, CancellationToken cancellationToken)
    {
        return await TerminalAsync(runId, RunStatuses.Succeeded, RunEventTypes.Completed, request.EventId,
            () => JsonSerializer.Serialize(request, PayloadJson), request.OccurredAt, request.SessionFilePath,
            db, storage, komodo,
            run => ValidateAndApplyCompletedAsync(run, request, db, storage, cancellationToken),
            cancellationToken);
    }

    public static async Task<IResult> FailedAsync(
        Guid runId, FailedRequest request, AppDbContext db, RuntimeStorageService storage, IKomodoClient komodo, CancellationToken cancellationToken)
    {
        return await TerminalAsync(runId, RunStatuses.Failed, RunEventTypes.Failed, request.EventId,
            () => JsonSerializer.Serialize(request, PayloadJson), request.OccurredAt, request.SessionFilePath,
            db, storage, komodo,
            run =>
            {
                run.ErrorCode = request.Error.Code;
                run.ErrorMessage = request.Error.Message;
                run.ResultSummary = Truncate(request.Error.Message, 512);
                return Task.FromResult<IResult?>(null);
            }, cancellationToken);
    }

    private static async Task<IResult> TerminalAsync(
        Guid runId,
        string runStatus,
        string eventType,
        string eventId,
        Func<string> serializePayload,
        DateTime occurredAt,
        string? sessionFilePath,
        AppDbContext db,
        RuntimeStorageService storage,
        IKomodoClient komodo,
        Func<Run, Task<IResult?>> apply,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(eventId))
            return RunPipelineApi.Error(400, "validation", "eventId is required");

        var runTaskId = await db.Runs.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => r.TaskId)
            .SingleOrDefaultAsync(cancellationToken);
        if (runTaskId == Guid.Empty)
            return RunPipelineApi.Error(404, "not_found", $"run {runId} does not exist");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var run = await db.Runs.SingleAsync(r => r.Id == runId, cancellationToken);
            if (run.Status is RunStatuses.Succeeded or RunStatuses.Failed or RunStatuses.Cancelled or RunStatuses.TimedOut)
            {
                var alreadyDelivered = await db.RunEvents.AsNoTracking()
                    .AnyAsync(e => e.RunId == runId && e.EventId == eventId, cancellationToken);
                if (alreadyDelivered)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return Results.Json(new { persisted = true }); // idempotent redelivery of the terminal callback
                }
                return RunPipelineApi.Error(409, "invalid_transition", $"run {runId} is already terminal ('{run.Status}')");
            }

            db.RunEvents.Add(new RunEvent
            {
                Id = Guid.NewGuid(),
                RunId = runId,
                EventId = eventId,
                Type = eventType,
                Payload = serializePayload(),
                OccurredAt = occurredAt,
                ReceivedAt = DateTime.UtcNow,
            });
            run.Status = runStatus;
            run.CompletedAt = DateTime.UtcNow;
            run.LastProgressAt = DateTime.UtcNow;

            var failure = await apply(run);
            if (failure is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return failure;
            }

            await FinalizeTerminalRunAsync(db, run, storage, cancellationToken);
            // decision 57 (ticket 12): record the Bridge-discovered session path when the agent
            // reported one — the provisioning-time prediction is provisional, this is the actual path.
            if (!string.IsNullOrWhiteSpace(sessionFilePath))
                await db.AgentSessions.Where(s => s.RunId == runId)
                    .ExecuteUpdateAsync(set => set.SetProperty(s => s.SessionFilePath, sessionFilePath.Trim()),
                        cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (JsonException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return RunPipelineApi.Error(400, "validation", "callback body is not valid JSON");
        }

        // After the persistence the Bridge relies on, release the container: desired state stays
        // Orchestrator-owned, the observed machine and the stop are Runtime-owned facts.
        var container = await db.ContainerInstances
            .Where(c => c.RunId == runId)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (container?.DeploymentName is { } deployment)
        {
            var stopped = await komodo.StopAsync(deployment, cancellationToken);
            if (stopped)
            {
                container.ObservedState = ContainerObservedStates.Stopped;
                container.LastStoppedAt = DateTime.UtcNow;
            }
            await db.Workspaces
                .Where(w => w.TaskId == runTaskId && w.Status == WorkspaceStatuses.InUse)
                .ExecuteUpdateAsync(set =>
                {
                    set.SetProperty(w => w.Status, WorkspaceStatuses.Ready);
                    set.SetProperty(w => w.LastUsedAt, DateTime.UtcNow);
                }, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        return Results.Json(new { persisted = true });
    }

    /// <summary>completed: validate the manifest, create Artifact rows + `artifact_produced` events.
    /// Called inside the outer transaction; returns an error result to reject without persistence.</summary>
    private static async Task<IResult?> ValidateAndApplyCompletedAsync(
        Run run, CompletedRequest request, AppDbContext db, RuntimeStorageService storage, CancellationToken cancellationToken)
    {
        if (request.Result is null)
            return RunPipelineApi.Error(400, "validation", "result is required");
        if (string.IsNullOrWhiteSpace(request.Result.Summary))
            return RunPipelineApi.Error(400, "validation", "result.summary is required");
        if (request.Outcome is { } outcome && outcome is not ("completed" or "needs_human_input"))
            return RunPipelineApi.Error(400, "validation", "outcome must be 'completed' or 'needs_human_input'");

        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == run.TaskId, cancellationToken);
        var workspace = await db.Workspaces.AsNoTracking().SingleAsync(w => w.TaskId == task.Id, cancellationToken);
        var workflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == task.WorkflowId, cancellationToken);
        // the Runtime API reads the workspace through its OWN mount (Storage.ManagedRoot); the row's
        // storage_reference stays the host-reference shape decision 52 wants on the record
        var workspaceHostPath = storage.WorkspacePath(workflow.Id, task.Id);

        var artifacts = new List<Artifact>();
        foreach (var manifestArtifact in request.Result.Artifacts)
        {
            if (manifestArtifact.Type is null || manifestArtifact.Format is null || manifestArtifact.Path is null)
                return RunPipelineApi.Error(400, "validation", "result.artifacts[] entries need type/format/path");
            if (!ManifestArtifactTypes.Contains(manifestArtifact.Type)) // log_reference is Runtime-API generated
                return RunPipelineApi.Error(422, "manifest_invalid", $"artifact type '{manifestArtifact.Type}' is not a valid manifest artifact type");

            // in-place validation (04 §4): inside the workspace, present, ≤256 KiB — no silent truncation
            var absolute = Path.GetFullPath(Path.Combine(workspaceHostPath, manifestArtifact.Path));
            if (Path.GetRelativePath(workspaceHostPath, absolute).StartsWith("..", StringComparison.Ordinal))
                return RunPipelineApi.Error(422, "manifest_invalid", $"artifact path '{manifestArtifact.Path}' escapes the workspace");
            if (!File.Exists(absolute))
                return RunPipelineApi.Error(422, "manifest_invalid", $"artifact file '{manifestArtifact.Path}' does not exist in the workspace");
            var bytes = File.ReadAllBytes(absolute);
            if (bytes.Length > MaxArtifactBytes)
                return RunPipelineApi.Error(422, "artifact_too_large",
                    $"artifact '{manifestArtifact.Path}' is {bytes.Length} bytes; the limit is {MaxArtifactBytes}");
            if (ArtifactFormats.Markdown != manifestArtifact.Format && ArtifactFormats.Json != manifestArtifact.Format && ArtifactFormats.Text != manifestArtifact.Format)
                return RunPipelineApi.Error(422, "manifest_invalid", $"artifact format '{manifestArtifact.Format}' is not supported in V1");

            artifacts.Add(new Artifact
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                WorkflowId = workflow.Id,
                Type = manifestArtifact.Type,
                Format = manifestArtifact.Format,
                Content = Encoding.UTF8.GetString(bytes),
                Metadata = JsonSerializer.Serialize(new
                {
                    path = manifestArtifact.Path,
                    runId = run.Id,
                    bytes = bytes.Length,
                    branch = request.Result.Branch,
                    commitSha = request.Result.CommitSha,
                }),
                CreatedAt = DateTime.UtcNow,
            });
        }

        run.ResultSummary = Truncate(request.Result.Summary, 512);

        foreach (var artifact in artifacts)
        {
            db.Artifacts.Add(artifact);
            db.RunEvents.Add(new RunEvent
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                EventId = $"artifact:{artifact.Id}",
                Type = RunEventTypes.ArtifactProduced,
                Payload = JsonSerializer.Serialize(new
                {
                    artifactId = artifact.Id,
                    taskId = artifact.TaskId,
                    type = artifact.Type,
                    format = artifact.Format,
                }),
                OccurredAt = DateTime.UtcNow,
                ReceivedAt = DateTime.UtcNow,
            });
        }
        return null;
    }

    /// <summary>Terminal facts every terminal callback records: log trio fields, the `log_reference` Artifact,
    /// credential revocation (decision 60), and the session's `terminated` state.</summary>
    private static async Task FinalizeTerminalRunAsync(
        AppDbContext db, Run run, RuntimeStorageService storage, CancellationToken cancellationToken)
    {
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == run.TaskId, cancellationToken);
        var workflowId = task.WorkflowId;
        var logsDir = $"{storage.Options.ManagedRoot}/{workflowId}/runs/{run.Id}/logs";
        var now = DateTime.UtcNow;

        // decision 43: the reference points at the per-Run log directory; the size is the trio's total on
        // disk; content is never copied into PostgreSQL.
        run.LogStorageReference = logsDir;
        run.LogSizeBytes = Directory.Exists(logsDir) ? RuntimeStorageService.DirectorySizeBytes(logsDir) : null;
        run.LogsCompletedAt = now;

        db.Artifacts.Add(new Artifact
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            WorkflowId = workflowId,
            Type = ArtifactTypes.LogReference,
            Format = ArtifactFormats.Text,
            Content = logsDir,
            Metadata = JsonSerializer.Serialize(new { runId = run.Id, bytes = run.LogSizeBytes, logDirectory = logsDir }),
            CreatedAt = now,
        });
        db.RunEvents.Add(new RunEvent
        {
            Id = Guid.NewGuid(),
            RunId = run.Id,
            EventId = $"artifact:log-reference-{run.Id}",
            Type = RunEventTypes.ArtifactProduced,
            Payload = JsonSerializer.Serialize(new { artifactType = ArtifactTypes.LogReference, runId = run.Id }),
            OccurredAt = now,
            ReceivedAt = DateTime.UtcNow,
        });

        await RunCredentialService.RevokeAsync(db, run.Id, cancellationToken);
        await db.AgentSessions.Where(s => s.RunId == run.Id)
            .ExecuteUpdateAsync(set =>
            {
                set.SetProperty(s => s.Status, AgentSessionStatuses.Terminated);
                set.SetProperty(s => s.LastActivityAt, now);
            }, cancellationToken);
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    /// <summary>Persist one RunEvent when its idempotency key is fresh; returns a 200 result for duplicates.</summary>
    private static async Task<IResult?> PersistEventAsync(
        AppDbContext db, Guid runId, string eventId, string type, string? payload, DateTime occurredAt, CancellationToken cancellationToken)
    {
        var duplicate = await db.RunEvents.AsNoTracking()
            .AnyAsync(e => e.RunId == runId && e.EventId == eventId, cancellationToken);
        if (duplicate)
            return Results.Json(new { persisted = true }); // duplicate eventIds are ignored (04 §8)

        db.RunEvents.Add(new RunEvent
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            EventId = eventId,
            Type = type,
            Payload = payload,
            OccurredAt = occurredAt,
            ReceivedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
        return null;
    }
}
