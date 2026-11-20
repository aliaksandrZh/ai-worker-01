using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using PiAgent.Infrastructure;
using PiAgent.Platform.Auth;
using PiAgent.Platform.Http;
using PiAgent.Platform.Logging;
using PiAgent.Platform.Workers;
using PiAgent.RuntimeApi;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddPiJsonLogging("runtime-api");
builder.Services.AddPiData(builder.Configuration);
builder.Services.AddServiceTokens(builder.Configuration);
builder.Services.AddPiWorkerLoop();
builder.Services.Configure<KomodoOptions>(builder.Configuration.GetSection("Komodo"));
builder.Services.AddHttpClient("komodo");
builder.Services.AddSingleton<IReadinessCheck, KomodoReadinessCheck>();
builder.Services.AddSingleton<DbSizeCycle>();
builder.Services.AddSingleton<IWorkerCycle>(serviceProvider => serviceProvider.GetRequiredService<DbSizeCycle>());
builder.Services.AddSingleton<IWorkerMetrics, RuntimeApiMetrics>();

var app = builder.Build();
app.MapPiEndpoints();
app.Run();

public partial class Program;
