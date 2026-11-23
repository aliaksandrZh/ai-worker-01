using PiAgent.HostMonitor;
using PiAgent.Platform.Http;
using PiAgent.Platform.Logging;
using PiAgent.Platform.Workers;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddPiJsonLogging("host-monitor");
builder.Services.AddDockerSocket(builder.Configuration);
builder.Services.Configure<HostMonitorOptions>(builder.Configuration.GetSection("Storage"));
// Where the telemetry goes + the service credential that protects it.
builder.Services.Configure<RuntimeApiOptions>(builder.Configuration.GetSection("RuntimeApi"));
builder.Services.AddHttpClient("host-monitor");
builder.Services.AddSingleton<IDockerMonitor, DockerMonitorClient>();
builder.Services.AddSingleton<IReadinessCheck, StorageReadinessCheck>();
builder.Services.AddPiWorkerLoop();
builder.Services.AddSingleton<HostMonitorCycle>();
builder.Services.AddSingleton<IWorkerCycle>(serviceProvider => serviceProvider.GetRequiredService<HostMonitorCycle>());
builder.Services.AddSingleton<IWorkerMetrics, HostMonitorMetrics>();

var app = builder.Build();
app.MapPiEndpoints();
app.Run();

public partial class Program;
