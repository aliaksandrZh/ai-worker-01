using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Infrastructure;
using PiAgent.Orchestrator;
using PiAgent.Orchestrator.Api;
using PiAgent.Orchestrator.Builds;
using PiAgent.Orchestrator.Commands;
using PiAgent.Orchestrator.Config;
using PiAgent.Orchestrator.Orchestration;
using PiAgent.Platform.Auth;
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
// Command pipeline (task 06): grammar + authorization over synchronized comment events.
builder.Services.AddSingleton<CommandMetrics>();
// Decision-65 internal contour (task 08): the Orchestration API + /internal/commands hook are service-token guarded.
builder.Services.AddServiceTokens(builder.Configuration);
builder.Services.AddSingleton(sp =>
{
    var patFile = sp.GetRequiredService<IOptions<ConfigLoaderOptions>>().Value.PatFile;
    var pat = SyncPat.Read(patFile); // the single read-only PAT (decision 65); missing/empty fails start
    var httpClient = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
    return new CollaboratorPermissionGate(httpClient, pat);
});
builder.Services.AddSingleton<IWorkerCycle, CommentCommandProcessor>();
// Durable job queue (task 07): admission hands commands to Task+Job rows; this worker claims and prepares.
builder.Services.AddOptions<JobQueueOptions>().BindConfiguration("JobQueue");
builder.Services.AddSingleton<JobQueueMetrics>();
// Runtime dispatch (ticket 09): decision-65 service credential + the terminal-Run consumer.
builder.Services.Configure<RuntimeApiOptions>(builder.Configuration.GetSection("RuntimeApi"));
builder.Services.AddHttpClient<RuntimeApiClient>(client => client.Timeout = TimeSpan.FromSeconds(200));
builder.Services.AddSingleton<IRunDispatcher, RuntimeApiRunDispatcher>();
builder.Services.AddSingleton<IWorkerCycle, RunTerminalConsumer>();
// Profile image builds (ticket 10, decision 81): durable BuildJobs, async dispatch + polling against
// the Build Worker's internal build API (decision-65 service credential, same direction as task 09).
builder.Services.Configure<BuildWorkerOptions>(builder.Configuration.GetSection("BuildWorker"));
builder.Services.AddHttpClient<BuildWorkerApiClient>(client => client.Timeout = TimeSpan.FromSeconds(130));
builder.Services.AddSingleton<BuildMetrics>();
builder.Services.AddSingleton<IWorkerCycle, ProfileBuildWorker>();
builder.Services.AddSingleton<IWorkerCycle, JobQueueWorker>();

var app = builder.Build();
app.MapPiEndpoints();
app.MapOrchestrationApi();
app.Run();

public partial class Program;
