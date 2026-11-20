using PiAgent.Infrastructure;
using PiAgent.Orchestrator;
using PiAgent.Orchestrator.Config;
using PiAgent.Platform.Http;
using PiAgent.Platform.Logging;
using PiAgent.Platform.Workers;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddPiJsonLogging("orchestrator");
builder.Services.AddPiData(builder.Configuration);
// §13 cold-start gate: the Config Loader runs FIRST (before the server accepts requests and any
// worker tick); until it succeeds, the gate stays closed (01 §13, decisions 55/66/81).
builder.Services.AddPiConfigLoader(builder.Configuration);
builder.Services.AddPiWorkerLoop();
builder.Services.AddSingleton<IWorkerMetrics, OrchestratorMetrics>();

var app = builder.Build();
app.MapPiEndpoints();
app.Run();

public partial class Program;
