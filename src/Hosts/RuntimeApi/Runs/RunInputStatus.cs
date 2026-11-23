using PiAgent.RuntimeApi.Provisioning;
using PiAgent.RuntimeApi.Runs;
using PiAgent.RuntimeApi.Api;
using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;

namespace PiAgent.RuntimeApi.Runs;

/// <summary>
/// `GET /internal/runtime/runs/{runId}/input` (bridge pull channel, per-Run bearer): serves the frozen
/// `RunContextSnapshot` content the Orchestrator assembled before dispatch. Read-only, idempotent, and
/// safe to call repeatedly (restart recovery pulls the same bytes).
/// </summary>
public static class InputHandler
{
    public static async Task<IResult> HandleAsync(Guid runId, AppDbContext db, CancellationToken cancellationToken)
    {
        var snapshot = await db.RunContextSnapshots.AsNoTracking()
            .SingleOrDefaultAsync(s => s.RunId == runId, cancellationToken);
        if (snapshot is null)
            return RunPipelineApi.Error(404, "not_found", $"no RunContextSnapshot exists for run {runId}");
        return Results.Text(snapshot.Content, "application/json");
    }
}

/// <summary>
/// `GET /internal/runtime/runs/{runId}` (04 §3): desired/observed runtime status, heartbeat, container
/// reference, and error details as Runtime-owned facts (business transitions stay with the Orchestrator).
/// </summary>
public static class RunStatusHandler
{
    public static async Task<IResult> HandleAsync(Guid runId, AppDbContext db, CancellationToken cancellationToken)
    {
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null)
            return RunPipelineApi.Error(404, "not_found", $"run {runId} does not exist");

        var container = await db.ContainerInstances.AsNoTracking()
            .Where(c => c.RunId == runId)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
        var session = await db.AgentSessions.AsNoTracking()
            .SingleOrDefaultAsync(s => s.RunId == runId, cancellationToken);
        var lastEvent = await db.RunEvents.AsNoTracking()
            .Where(e => e.RunId == runId)
            .OrderByDescending(e => e.ReceivedAt)
            .Select(e => new { e.Type, e.ReceivedAt })
            .FirstOrDefaultAsync(cancellationToken);

        return Results.Json(new
        {
            runId = run.Id,
            status = run.Status,
            attemptNumber = run.AttemptNumber,
            startedAt = run.StartedAt,
            completedAt = run.CompletedAt,
            lastHeartbeatAt = run.LastHeartbeatAt,
            lastProgressAt = run.LastProgressAt,
            exitCode = run.ExitCode,
            errorCode = run.ErrorCode,
            errorMessage = run.ErrorMessage,
            resultSummary = run.ResultSummary,
            logStorageReference = run.LogStorageReference,
            logSizeBytes = run.LogSizeBytes,
            logsCompletedAt = run.LogsCompletedAt,
            container = container is null ? null : new
            {
                id = container.Id,
                desiredState = container.DesiredState,
                observedState = container.ObservedState,
                containerName = container.ContainerName,
                dockerContainerId = container.DockerContainerId,
                hostId = container.HostId,
                containerImage = container.ContainerImage,
                lastStartedAt = container.LastStartedAt,
                lastStoppedAt = container.LastStoppedAt,
            },
            session = session is null ? null : new
            {
                id = session.Id,
                status = session.Status,
                runtimeSessionId = session.RuntimeSessionId,
                sessionFilePath = session.SessionFilePath,
                lastHeartbeatAt = session.LastHeartbeatAt,
            },
            lastEvent,
        });
    }
}
