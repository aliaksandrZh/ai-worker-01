using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace PiAgent.Platform.Logging;

public static class LoggingServiceCollectionExtensions
{
    /// <summary>Configures the structured JSON console format for one service.</summary>
    public static ILoggingBuilder AddPiJsonLogging(this ILoggingBuilder builder, string serviceName)
    {
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
