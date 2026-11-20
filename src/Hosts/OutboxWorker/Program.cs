using PiAgent.Infrastructure;
using PiAgent.OutboxWorker;
using PiAgent.Platform.Http;
using PiAgent.Platform.Logging;
using PiAgent.Platform.Workers;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddPiJsonLogging("outbox-worker");
builder.Services.AddPiData(builder.Configuration);
builder.Services.AddPiWorkerLoop();
builder.Services.AddSingleton<IWorkerMetrics, OutboxMetrics>();

var app = builder.Build();
app.MapPiEndpoints();
app.Run();

public partial class Program;
