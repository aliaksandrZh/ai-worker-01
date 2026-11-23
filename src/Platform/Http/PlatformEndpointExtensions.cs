using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PiAgent.Platform.Logging;
using PiAgent.Platform.Workers;

namespace PiAgent.Platform.Http;

/// <summary>
/// Uniform operational endpoints every service exposes:
/// /healthz liveness, /readyz aggregated readiness, /metrics baseline.
/// </summary>
public static class PlatformEndpoints
{
    public static WebApplication MapPiEndpoints(this WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));

        app.MapGet("/readyz", async (IServiceProvider sp, CancellationToken cancellationToken) =>
        {
            var checks = sp.GetServices<IReadinessCheck>().ToList();
            if (checks.Count == 0)
                return Results.Json(new { status = "ready", checks = (object)new { } });

            var results = await Task.WhenAll(
                checks.Select(async check => (check.Name, await check.CheckAsync(cancellationToken))));

            var failed = results
                .Where(r => !r.Item2.Ok)
                .Select(r => new { name = r.Name, error = r.Item2.Error })
                .ToList();
            if (failed.Count > 0)
                return Results.Json(new { status = "notReady", failed }, statusCode: StatusCodes.Status503ServiceUnavailable);

            var ok = new Dictionary<string, object>();
            foreach (var (name, _) in results)
                ok.TryAdd(name, "ok");
            return Results.Json(new { status = "ready", checks = ok });
        });

        app.MapGet("/metrics", (IServiceProvider sp) =>
        {
            var service = sp.GetService<IOptions<PiJsonConsoleFormatterOptions>>()?.Value.ServiceName;
            var metrics = new Dictionary<string, object?>();
            foreach (var provider in sp.GetServices<IWorkerMetrics>())
                foreach (var (key, value) in provider.Collect())
                    metrics[key] = value;
            return Results.Json(new
            {
                service,
                generatedAtUtc = DateTimeOffset.UtcNow,
                metrics,
            });
        });

        return app;
    }
}
