using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using PiAgent.Infrastructure;
using PiAgent.Platform.Auth;
using PiAgent.Platform.Http;
using PiAgent.Platform.Logging;
using PiAgent.Platform.Workers;
using PiAgent.RuntimeApi;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddPiJsonLogging("runtime-api");
builder.Services.AddPiData(builder.Configuration);
builder.Services.AddServiceTokens(builder.Configuration);
builder.Services.AddPiWorkerLoop();
// Decision 82: the Runtime API provisions agent containers directly over the Docker socket
// (read-write in compose); the docker readiness check replaces the former Komodo Core check.
builder.Services.AddDockerSocket(builder.Configuration);
builder.Services.Configure<ContainerProvisionerOptions>(builder.Configuration.GetSection("AgentContainer"));
builder.Services.AddSingleton<IContainerProvisioner, DockerProvisioningClient>();
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
// Ticket 13: the §16 knobs (from the configuration plane's system.yaml) the reconcilers enforce.
builder.Services.AddSingleton(RuntimeLimits.Load(builder.Configuration["PI_AGENT_CONFIG_DIR"]));
builder.Services.AddSingleton<RuntimeStorageService>();
builder.Services.AddSingleton<RunCallbackTokenFilter>();
builder.Services.AddSingleton<DbSizeCycle>();
builder.Services.AddSingleton<IWorkerCycle>(serviceProvider => serviceProvider.GetRequiredService<DbSizeCycle>());
builder.Services.AddSingleton<RunReconcilerCycle>();
builder.Services.AddSingleton<IWorkerCycle>(serviceProvider => serviceProvider.GetRequiredService<RunReconcilerCycle>());
// Ticket 13: the execution-limit reconciler (startup/idle/max_run_duration) + the decision-77 orphan
// reconciler (grace/quarantine/delete behind Host-Monitor telemetry).
builder.Services.AddSingleton<TimeoutSweeper>();
builder.Services.AddSingleton<IWorkerCycle, TimeoutSweeper>();
builder.Services.AddSingleton<OrphanReconciler>();
builder.Services.AddSingleton<IWorkerCycle, OrphanReconciler>();
// Ticket 14: reconciliation leg of the graceful stop — Runs never confirming their stop are cancelled.
builder.Services.AddSingleton<StopConfirmSweeper>();
builder.Services.AddSingleton<IWorkerCycle, StopConfirmSweeper>();
builder.Services.AddSingleton<RunPipelineMetrics>();
builder.Services.AddSingleton<IWorkerMetrics, RuntimeApiMetrics>();

var app = builder.Build();
app.MapPiEndpoints();
app.MapRunPipelineApi();
app.MapMonitorTelemetryApi();
app.Run();

public partial class Program;
