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
/// (heartbeat/events/completed/failed/cancelled — every delivery persisted as a RunEvent before
/// acknowledgement), `GET /internal/runtime/runs/{runId}/input` (bridge pull channel), the per-Run
/// `GET …/stop-request` (the ticket-14 Bridge control channel), and the service-token `POST …/stop`
/// + `GET …/status`. Errors use the standard 04 §7 shape `{code, message, details}`.
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
        app.MapPost("/internal/runtime/runs/{runId:guid}/cancelled", (Guid runId, CancelledRequest request, AppDbContext db, RuntimeStorageService storage, IKomodoClient komodo, CancellationToken ct) =>
                CallbackHandler.CancelledAsync(runId, request, db, storage, komodo, ct))
            .AddEndpointFilter<RunCallbackTokenFilter>();
        app.MapPost("/internal/runtime/runs/{runId:guid}/stop", StopRunHandler.HandleAsync)
            .RequireServiceToken();
        app.MapGet("/internal/runtime/runs/{runId:guid}/stop-request", StopRequestHandler.HandleAsync)
            .AddEndpointFilter<RunCallbackTokenFilter>();
        app.MapGet("/internal/runtime/runs/{runId:guid}", RunStatusHandler.HandleAsync)
            .RequireServiceToken();
    }

    public static IResult Error(int statusCode, string code, string message, object? details = null) =>
        Results.Json(new { code, message, details = details ?? new { } }, statusCode: statusCode);

    /// <summary>
    /// `POST /internal/runtime/runs/{runId}/stop` (04 §3, ticket 14 — decision 61): records the
    /// Orchestrator→Runtime API control signal (run.stop_requested_at/stop_reason, the reconciliation
    /// timeline facts) and moves the Run to `stopping` (decision 75: awaiting confirmed stop; the
    /// capacity slot stays held until the terminal callback confirms). It does NOT stop the container:
    /// the Bridge performs the graceful sequence (poll `/stop-request` → clear_queue → abort → settled
    /// → stdin → SIGTERM/SIGKILL → verified process tree) and posts the terminal `cancelled` callback,
    /// whose delivery stops the container; the stop-confirm sweeper backstops a Bridge that never
    /// delivers. Idempotent: the signal is recorded once; already-terminal runs return their state.
    /// </summary>
    public static class StopRunHandler
    {
        public static async Task<IResult> HandleAsync(Guid runId, StopRunRequest request, AppDbContext db, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.Reason))
                return Error(400, "validation", "reason is required");

            var run = await db.Runs.SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
            if (run is null)
                return Error(404, "not_found", $"run {runId} does not exist");
            if (run.Status is RunStatuses.Succeeded or RunStatuses.Failed or RunStatuses.Cancelled or RunStatuses.TimedOut)
                return Results.Json(new { runId, status = run.Status }); // idempotent: already terminal

            if (run.StopRequestedAt is null)
            {
                var now = DateTime.UtcNow;
                run.StopRequestedAt = now; // recorded once — subsequent calls are read-only no-ops
                run.StopReason = request.Reason;
                run.Status = RunStatuses.Stopping;
                await db.SaveChangesAsync(cancellationToken);
            }
            return Results.Json(new { runId, status = run.Status });
        }
    }

    /// <summary>`GET /internal/runtime/runs/{runId}/stop-request` (per-Run bearer): the Bridge's
    /// pull channel for the stop signal (04 §4 graceful stop; the signal state lives in PostgreSQL).</summary>
    public static class StopRequestHandler
    {
        public static async Task<IResult> HandleAsync(Guid runId, AppDbContext db, CancellationToken cancellationToken)
        {
            var run = await db.Runs.AsNoTracking()
                .Where(r => r.Id == runId)
                .Select(r => new { r.StopRequestedAt, r.StopReason })
                .SingleOrDefaultAsync(cancellationToken);
            if (run is null)
                return Error(404, "not_found", $"run {runId} does not exist");
            return Results.Json(new
            {
                requested = run.StopRequestedAt is not null,
                reason = run.StopReason,
                requestedAt = run.StopRequestedAt,
            });
        }
    }
}
