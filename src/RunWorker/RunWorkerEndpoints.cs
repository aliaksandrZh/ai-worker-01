using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PiAgent.Events;

namespace PiAgent.RunWorker;

/// <summary>
/// The one HTTP surface of the runtime (docs/run-worker.md): the Bridge's egress.
/// Auth = the per-run token minted at run_scheduled; the token authorizes appending
/// events for that run and nothing else.
/// </summary>
public static class RunWorkerEndpoints
{
    public static WebApplication MapAgentEventsEndpoint(this WebApplication app, RunRegistry registry, IEventLog log)
    {
        app.MapPost("/events", async (HttpContext http, CancellationToken ct) =>
        {
            if (!http.Request.Headers.TryGetValue("X-Run-Token", out var token))
                return Results.Unauthorized();

            var entry = registry.ByToken(token!);
            if (entry is null)
                return Results.Unauthorized();

            var payload = await http.Request.ReadFromJsonAsync<CallbackRequest>(ct);
            if (payload is null)
                return Results.BadRequest(new { error = "body required" });

            await log.AppendAsync(new AgentCallback(entry.RunId, payload.Route, payload.Body), ct);
            return Results.Accepted();
        });

        return app;
    }

    private sealed record CallbackRequest(string Route, string? Body);
}
