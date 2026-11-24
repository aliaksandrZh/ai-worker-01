using PiAgent.Poller.GitHub;
using PiAgent.Poller.Streams;
using Microsoft.Extensions.Options;
using PiAgent.Infrastructure;
using PiAgent.Poller;
using PiAgent.Platform.Http;
using PiAgent.Platform.Logging;
using PiAgent.Platform.Workers;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddPiJsonLogging("poller");
builder.Services.AddPiData(builder.Configuration);
builder.Services.AddPiWorkerLoop();
builder.Services.AddSingleton<IWorkerMetrics, PollerMetrics>();

// Startup configuration (same stance as the Outbox Worker / Config Loader): polling knobs + the
// one read-only sync PAT are read ONCE — missing/malformed
// values fail host startup with a diagnosable error.
builder.Services.AddSingleton(Options.Create(PollerConfig.Read(
    builder.Configuration["PI_AGENT_CONFIG_DIR"] ?? "config")));
builder.Services.AddSingleton(new GitHubReadClient(
    new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(30) },
    PiAgent.Platform.Secrets.PatFile.Read(builder.Configuration["PI_AGENT_GITHUB_PAT_FILE"], "PI_AGENT_GITHUB_PAT_FILE",
    // the fine-grained read-only sync PAT — messages preserved verbatim
    "the Poller syncs with a fine-grained read-only PAT")));

builder.Services.AddSingleton(new PollerState());
// Projects v2 boards (projects.yaml): which user-owned boards the projects pass lists and diffs.
builder.Services.AddSingleton(Options.Create(ProjectSyncConfig.Read(builder.Configuration["PI_AGENT_CONFIG_DIR"] ?? "config")));
builder.Services.AddSingleton<IRepositoryStream, IssuesStream>();
builder.Services.AddSingleton<IRepositoryStream, PullRequestsStream>();
builder.Services.AddSingleton<IRepositoryStream, PrReviewsStream>();
builder.Services.AddSingleton<IRepositoryStream, IssueCommentsStream>();
builder.Services.AddSingleton<IRepositoryStream, PrReviewCommentsStream>();
builder.Services.AddSingleton<IRepositoryStream, TimelineStream>();
builder.Services.AddSingleton<IRepositoryStream, DeletedCommentSweepStream>();
builder.Services.AddSingleton<IWorkerCycle, PollerCycle>();
// Projects pass AFTER the repository-streams cycle: it honors the rate-limit pause that cycle sets.
builder.Services.AddSingleton<IWorkerCycle, ProjectItemsCycle>();

var app = builder.Build();
app.MapPiEndpoints();
app.Run();

public partial class Program;
