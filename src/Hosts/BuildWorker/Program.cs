using PiAgent.BuildWorker;
using PiAgent.Platform.Http;
using PiAgent.Platform.Logging;
using PiAgent.Platform.Workers;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddPiJsonLogging("build-worker");
builder.Services.AddDockerSocket(builder.Configuration);
builder.Services.AddPiWorkerLoop();
builder.Services.AddSingleton<IWorkerMetrics, BuildWorkerMetrics>();

var app = builder.Build();
app.MapPiEndpoints();
app.Run();

public partial class Program;
