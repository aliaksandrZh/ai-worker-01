using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using PiAgent.BuildWorker;
using PiAgent.Platform.Auth;
using PiAgent.Platform.Http;
using PiAgent.Platform.Logging;
using PiAgent.Platform.Workers;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddPiJsonLogging("build-worker");
// §13 dependencies: the Docker API over its (read-only mounted) unix socket + durable local build state.
builder.Services.AddDockerSocket(builder.Configuration);
builder.Services.Configure<BuildOptions>(builder.Configuration.GetSection("Build"));
builder.Services.AddPiWorkerLoop();
builder.Services.AddSingleton<IWorkerMetrics, BuildWorkerMetrics>();
builder.Services.AddSingleton<RunnerCounters>();
// Decision-65 contour: the orchestrator's own service credential guards the two build endpoints.
builder.Services.AddServiceTokens(builder.Configuration);
// Build mechanics (decision 81): durable local state, single-consumer runner, docker over the socket.
builder.Services.AddSingleton<IDockerEngine, DockerEngineClient>();
builder.Services.AddSingleton<BuildDurableStore>();
builder.Services.AddSingleton<BuildChannel>();
builder.Services.AddHostedService<BuildJobRunner>();
builder.Services.AddSingleton<IWorkerCycle, BuildJobSweep>();

var app = builder.Build();
app.MapPiEndpoints();
app.MapBuildApi();
app.Run();

public partial class Program;
