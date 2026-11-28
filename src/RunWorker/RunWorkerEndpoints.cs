using System.Text.Json;
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
    public static WebApplication MapAgentEventsEndpoint(this WebApplication app, RunRegistryStore store, IEventLog log)
    {
        app.MapPost("/events", async (HttpContext http, CancellationToken ct) =>
        {
            if (!http.Request.Headers.TryGetValue("X-Run-Token", out var token))
                return Results.Unauthorized();

            var entry = await store.GetByTokenAsync(token!, ct);
            if (entry is null)
                return Results.Unauthorized();

            var payload = await http.Request.ReadFromJsonAsync<CallbackRequest>(ct);
            if (payload is null)
                return Results.BadRequest(new { error = "body required" });

            // the Bridge sends the callback body as a JSON object (the route's body of
            // docs/run-worker.md); the callback row carries its raw JSON text
            await log.AppendAsync(
                new AgentCallback
                {
                    RunId = entry.RunId,
                    Route = payload.Route,
                    Body = payload.Body?.GetRawText(),
                    EventId = ParseEventId(payload.Body?.GetRawText()),
                }, ct);
            return Results.Accepted();
        });

        return app;
    }

    /// <summary>
    /// The Bridge's deterministic eventId, promoted onto the callback row: spool redelivery
    /// repeats it verbatim and the ingestion dedup index (type, payload->>'eventId') keys on
    /// it (docs/run-worker.md route tool). A body without one simply does not participate.
    /// </summary>
    private static string? ParseEventId(string? body)
    {
        if (body is null)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("eventId", out var id)
                   && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record CallbackRequest(string Route, JsonElement? Body);
}
