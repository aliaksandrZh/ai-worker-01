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
// Cold-start gate: the Config Loader runs FIRST (before the server accepts requests and any
// worker tick); until it succeeds, the gate stays closed.
builder.Services.AddPiConfigLoader(builder.Configuration);
builder.Services.AddPiWorkerLoop();
builder.Services.AddSingleton<IWorkerMetrics, OrchestratorMetrics>();
// Command pipeline (task 06): grammar + authorization over synchronized comment events.
builder.Services.AddSingleton<CommandMetrics>();
// Internal contour: the Orchestration API + /internal/commands hook are service-token guarded.
builder.Services.AddServiceTokens(builder.Configuration);
builder.Services.AddSingleton(sp =>
{
    var patFile = sp.GetRequiredService<IOptions<ConfigLoaderOptions>>().Value.PatFile;
    var pat = PiAgent.Platform.Secrets.PatFile.Read(patFile, "PI_AGENT_GITHUB_PAT_FILE",
        "the Config Loader must verify the bot identity against GET /user");
    var httpClient = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
    return new CollaboratorPermissionGate(httpClient, pat);
});
builder.Services.AddSingleton<IWorkerCycle, CommentCommandProcessor>();
builder.Services.AddSingleton<IWorkerCycle, RootClosureCycle>();
// Projects v2 events (sync + logs scope): logged and settled, no orchestration state touched.
builder.Services.AddSingleton<IWorkerCycle, ProjectEventLogCycle>();
builder.Services.AddSingleton<IWorkerCycle, ReviewTriggerCycle>();
// AI-scope labels (AI:Plan/AI:Code/AI:Review/AI:Test): the standing convergence scanner — no
// events claimed, conditions re-derived from the synced state on every tick.
builder.Services.AddSingleton<IWorkerCycle, LabelTriggerCycle>();
// Durable job queue (task 07): admission hands commands to Task+Job rows; this worker claims and prepares.
builder.Services.AddOptions<JobQueueOptions>().BindConfiguration("JobQueue");
builder.Services.AddSingleton<JobQueueMetrics>();
// Runtime dispatch: service credential + the terminal-Run consumer.
builder.Services.Configure<RuntimeApiOptions>(builder.Configuration.GetSection("RuntimeApi"));
builder.Services.AddHttpClient<RuntimeApiClient>(client => client.Timeout = TimeSpan.FromSeconds(200));
builder.Services.AddSingleton<IRunDispatcher, RuntimeApiRunDispatcher>();
builder.Services.AddSingleton<IWorkerCycle, RunTerminalConsumer>();
// Profile image builds: durable BuildJobs, async dispatch + polling against
// the Build Worker's internal build API (service credential, same direction as the runtime dispatch).
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
