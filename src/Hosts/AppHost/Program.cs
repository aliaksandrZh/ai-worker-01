using Microsoft.Extensions.Hosting;
using PiAgent.Events.Delivery;
using PiAgent.Events.Postgres;
using PiAgent.Platform.Http;
using PiAgent.Platform.Logging;

// The single app host (docs/adr/0004): EventLog consumers (Poller, Orchestration, Run
// Worker, Publisher, Build Worker) will register here stage by stage; the skeleton only
// owns the log + platform endpoints.

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddPiJsonLogging("app-host");
builder.Services.AddEventLog(builder.Configuration);

builder.Services.AddHostedService<EventLogSchemaBootstrap>();

var app = builder.Build();
app.MapPiEndpoints();
app.Run();

/// <summary>Idempotent event-log DDL before any consumer starts.</summary>
internal sealed class EventLogSchemaBootstrap(EventQueueStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => store.EnsureSchemaAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public partial class Program;
