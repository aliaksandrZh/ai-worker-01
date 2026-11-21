using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Auth;

namespace PiAgent.RuntimeApi;

/// <summary>
/// Error handling + endpoint mapping for the Runtime Run Pipeline (04 §3–§4): the single
/// service-token-guarded `POST /internal/runtime/runs/{runId}/start`, the per-Run bearer callbacks
/// (heartbeat/events/completed/failed — every delivery persisted as a RunEvent before acknowledgement),
/// `GET /internal/runtime/runs/{runId}/input` (bridge pull channel), `POST …/stop` and `GET …/status`
/// (service token). Errors use the standard 04 §7 shape `{code, message, details}`.
/// </summary>
public static class RunPipelineApi
{
    public static void MapRunPipelineApi(this WebApplication app)
    {
        app.MapPost("/internal/runtime/runs/{runId:guid}/start", StartRunHandler.HandleAsync)
            .RequireServiceToken();
        app.MapGet("/internal/runtime/runs/{runId:guid}/input", InputHandler.HandleAsync)
            .AddEndpointFilter<RunCallbackTokenFilter>();
        app.MapPost("/internal/runtime/runs/{runId:guid}/heartbeat", (Guid runId, HeartbeatRequest request, AppDbContext db, CancellationToken ct) =>
                CallbackHandler.HeartbeatAsync(runId, request, db, ct))
            .AddEndpointFilter<RunCallbackTokenFilter>();
        app.MapPost("/internal/runtime/runs/{runId:guid}/events", (Guid runId, EventRequest request, AppDbContext db, CancellationToken ct) =>
                CallbackHandler.EventAsync(runId, request, db, ct))
            .AddEndpointFilter<RunCallbackTokenFilter>();
        app.MapPost("/internal/runtime/runs/{runId:guid}/completed", (Guid runId, CompletedRequest request, AppDbContext db, RuntimeStorageService storage, IKomodoClient komodo, CancellationToken ct) =>
                CallbackHandler.CompletedAsync(runId, request, db, storage, komodo, ct))
            .AddEndpointFilter<RunCallbackTokenFilter>();
        app.MapPost("/internal/runtime/runs/{runId:guid}/failed", (Guid runId, FailedRequest request, AppDbContext db, RuntimeStorageService storage, IKomodoClient komodo, CancellationToken ct) =>
                CallbackHandler.FailedAsync(runId, request, db, storage, komodo, ct))
            .AddEndpointFilter<RunCallbackTokenFilter>();
        app.MapPost("/internal/runtime/runs/{runId:guid}/stop", StopRunHandler.HandleAsync)
            .RequireServiceToken();
        app.MapGet("/internal/runtime/runs/{runId:guid}", RunStatusHandler.HandleAsync)
            .RequireServiceToken();
    }

    public static IResult Error(int statusCode, string code, string message, object? details = null) =>
        Results.Json(new { code, message, details = details ?? new { } }, statusCode: statusCode);

    public static class StopRunHandler
    {
        public static async Task<IResult> HandleAsync(Guid runId, StopRunRequest request, AppDbContext db, IKomodoClient komodo, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.Reason))
                return Error(400, "validation", "reason is required");

            var run = await db.Runs.SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
            if (run is null)
                return Error(404, "not_found", $"run {runId} does not exist");
            if (run.Status is RunStatuses.Succeeded or RunStatuses.Failed or RunStatuses.Cancelled or RunStatuses.TimedOut)
                return Results.Json(new { runId, status = run.Status }); // idempotent: already terminal

            var container = await db.ContainerInstances
                .Where(c => c.RunId == runId)
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (container?.DeploymentName is null)
                return Error(409, "invalid_transition", "run has no provisioned container to stop");
            if (container.ObservedState == ContainerObservedStates.Stopped)
                return Results.Json(new { runId, status = run.Status }); // idempotent: already stopped

            // ticket 14 owns the graceful Bridge stop sequence; ticket 09 performs the container stop and
            // records the observation. Run state stays non-terminal until its terminal callback arrives.
            var stopped = await komodo.StopAsync(container.DeploymentName!, cancellationToken);
            if (!stopped)
                return Error(502, "komodo_failure", "Komodo failed to stop the container");

            container.ObservedState = ContainerObservedStates.Stopped;
            container.LastStoppedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Results.Json(new { runId, status = run.Status });
        }
    }
}
