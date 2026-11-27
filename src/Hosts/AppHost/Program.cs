using Microsoft.Extensions.Hosting;
using PiAgent.Events.Delivery;
using PiAgent.Events.Postgres;
using PiAgent.GithubSync;
using PiAgent.Orchestration;
using PiAgent.RunWorker;
using PiAgent.Orchestration.State.Postgres;
using PiAgent.Publisher;
using PiAgent.Publisher.Postgres;
using PiAgent.Platform.Http;
using PiAgent.Platform.Logging;

// The single app host (docs/adr/0004): EventLog consumers register here stage by stage.
// So far: the EventLog + the Orchestrator reactor (docs/orchestrator.md).

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddPiJsonLogging("app-host");
builder.Services.AddEventLog(builder.Configuration);
builder.Services.AddOrchestrator(builder.Configuration);
builder.Services.AddPublisher(builder.Configuration);
builder.Services.AddGithubSync(builder.Configuration);

builder.Services.AddHostedService<SchemaBootstrap>();

// the worker's hosted services (recovery, consumer, watchdog) must start AFTER SchemaBootstrap:
// provisioning inserts into run_container (FK → orch_run) and reads the run input pointers
builder.Services.AddRunWorker(builder.Configuration);

var app = builder.Build();
app.MapPiEndpoints();
app.MapRunWorker();
app.Run();

/// <summary>Idempotent event-log + orchestration DDL before any consumer starts.</summary>
internal sealed class SchemaBootstrap(
    EventQueueStore eventStore,
    PostgresOrchestrationState orchestration,
    PostgresPublishMarkerStore markers,
    RunRegistryStore runRegistry) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await eventStore.EnsureSchemaAsync(cancellationToken);
        await orchestration.EnsureSchemaAsync(cancellationToken);
        await markers.EnsureSchemaAsync(cancellationToken);
        await runRegistry.EnsureSchemaAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public partial class Program;
