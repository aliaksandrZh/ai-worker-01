using PiAgent.HostMonitor;
using PiAgent.Platform.Http;
using PiAgent.Platform.Logging;
using PiAgent.Platform.Workers;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddPiJsonLogging("host-monitor");
builder.Services.AddDockerSocket(builder.Configuration);
builder.Services.Configure<HostMonitorOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.AddSingleton<IReadinessCheck, StorageReadinessCheck>();
builder.Services.AddPiWorkerLoop();
builder.Services.AddSingleton<IWorkerMetrics, HostMonitorMetrics>();

var app = builder.Build();
app.MapPiEndpoints();
app.Run();

public partial class Program;
