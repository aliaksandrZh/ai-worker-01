using Microsoft.Extensions.Options;
using PiAgent.Infrastructure;
using PiAgent.Infrastructure.Persistence;
using PiAgent.OutboxWorker;
using PiAgent.Platform.Workers;
using PiAgent.Platform.Http;
using PiAgent.Platform.Logging;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddPiJsonLogging("outbox-worker");
builder.Services.AddPiData(builder.Configuration);
builder.Services.AddPiWorkerLoop();
builder.Services.AddSingleton<IWorkerMetrics, OutboxMetrics>();
builder.Services.AddSingleton(new OutboxState());

// Startup configuration (plan rulings 2/5): the write PAT and the one §16 knob are read ONCE here —
// both fail the host startup with a diagnosable error (same stance as the Orchestrator's config plane).
builder.Services.AddSingleton(Options.Create(OutboxWorkerConfig.Read(
    builder.Configuration["PI_AGENT_CONFIG_DIR"] ?? "config")));
builder.Services.AddSingleton(new GitHubWriteClient(
    new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(30) },
    PatFile.Read(builder.Configuration["PI_AGENT_GITHUB_WRITE_PAT_FILE"], "PI_AGENT_GITHUB_WRITE_PAT_FILE")));
builder.Services.AddScoped<OutboxActionStore>();
builder.Services.AddSingleton<IWorkerCycle, OutboxDispatcher>();

var app = builder.Build();
app.MapPiEndpoints();
app.Run();

public partial class Program;
