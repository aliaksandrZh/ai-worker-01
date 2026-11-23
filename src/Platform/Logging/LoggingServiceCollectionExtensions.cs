using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace PiAgent.Platform.Logging;

public static class LoggingServiceCollectionExtensions
{
    /// <summary>Configures the structured JSON console format for one service.</summary>
    public static ILoggingBuilder AddPiJsonLogging(this ILoggingBuilder builder, string serviceName)
    {
        // EF Core logs every executed SQL command at Information; on a busy agent that floods
        // the log lake (see Grafana "Pi Agent Logs"), so keep only slow (>= warning) ones.
        builder.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);
        // Minimal APIs log "Writing value of type '<>f__AnonymousType1`1' as Json." at Information
        // on EVERY JSON response (category Microsoft.AspNetCore.Http.Results) — pure framework noise.
        builder.AddFilter("Microsoft.AspNetCore.Http.Results", LogLevel.Warning);
        // Kestrel/hosting/routing narrate EVERY HTTP request ("Request starting", "Executing endpoint",
        // "Request finished") at Information — the callback traffic alone (heartbeat/stop-request/events)
        // floods the log lake. Warnings+ only; the startup banner stays visible.
        builder.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.AddFilter("Microsoft.AspNetCore.Hosting.Lifetime", LogLevel.Information);
        // Npgsql logs every executed SQL command ("Command execution completed") at Information
        // (category Npgsql.Command) — same treatment as the EF Core noise above.
        builder.AddFilter("Npgsql", LogLevel.Warning);
        builder.AddConsole(console => console.FormatterName = CorrelationJsonConsoleFormatter.FormatterName);
        builder.AddConsoleFormatter<CorrelationJsonConsoleFormatter, PiJsonConsoleFormatterOptions>(options =>
        {
            options.ServiceName = serviceName;
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.ffffffZ";
            options.UseUtcTimestamp = true;
        });
        return builder;
    }
}
