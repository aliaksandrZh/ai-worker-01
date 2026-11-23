using PiAgent.RuntimeApi.Provisioning;
using PiAgent.RuntimeApi.Runs;
using PiAgent.RuntimeApi.Api;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Auth;

namespace PiAgent.RuntimeApi.Api;

/// <summary>Telemetry payloads posted by the Execution Host Monitor (01 §2/04 §4, decision 62).</summary>
public sealed record MonitorTelemetryRequest(DateTime ObservedAtUtc, MonitorContainerTelemetry[] Containers, MonitorStorageTelemetry? Storage);

public sealed record MonitorContainerTelemetry(
    string? ContainerId, string? Name, Guid? RunId, Guid? TaskId, string? State,
    double? CpuPercent, int? ProcessCount, string[]? Processes, string[]? Binds, bool Busy, string? Note);

public sealed record MonitorStorageTelemetry(
    string Root, long? FreeBytes, long? UsedBytes, MonitorWorkflowDirTelemetry[] Directories);

public sealed record MonitorWorkflowDirTelemetry(string Name, DateTime ModifiedAtUtc, long? SizeBytes);

/// <summary>
/// `POST /internal/runtime/monitor/telemetry` (04 §4, decision 62/65, ticket 13): the Execution Host
/// Monitor posts what the control plane cannot observe itself — container state, process trees/CPU,
/// bind mounts and managed-storage usage — under its own service credential. The Runtime API persists
/// the LATEST observation as a single-row upsert (PostgreSQL); the timeout sweeper reads per-run
/// activity facts from it, the orphan reconciler reads the directory/bind facts. Nothing here touches
/// business state.
/// </summary>
public static class MonitorTelemetryApi
{
    public static void MapMonitorTelemetryApi(this WebApplication app)
    {
        app.MapPost("/internal/runtime/monitor/telemetry", Handler.HandleAsync)
            .RequireServiceToken();
    }

    public static class Handler
    {
        public static async Task<IResult> HandleAsync(MonitorTelemetryRequest request, AppDbContext db, CancellationToken cancellationToken)
        {
            if (request.Containers is null)
                return RunPipelineApi.Error(400, "validation", "containers is required");

            var now = DateTime.UtcNow;
            var observation = await db.MonitorObservations.SingleOrDefaultAsync(cancellationToken);
            if (observation is null)
            {
                observation = new MonitorObservation { Id = Guid.NewGuid() };
                db.MonitorObservations.Add(observation);
            }
            observation.ObservedAt = request.ObservedAtUtc;
            observation.ReceivedAt = now;
            observation.Containers = JsonSerializer.Serialize(request.Containers, CallbackHandler.PayloadJson);
            observation.Storage = request.Storage is null ? null : JsonSerializer.Serialize(request.Storage, CallbackHandler.PayloadJson);
            observation.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return Results.Json(new { persisted = true });
        }
    }
}
