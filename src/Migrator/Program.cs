using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiAgent.Infrastructure;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddPiJsonLogging("migrator");
builder.Services.AddPiData(builder.Configuration);

var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Migrator");
var database = host.Services.GetRequiredService<AppDbContext>().Database;

await database.MigrateAsync();
logger.LogInformation("migrations applied successfully");
return 0;
