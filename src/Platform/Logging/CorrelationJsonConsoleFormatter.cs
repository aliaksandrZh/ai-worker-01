using System.Collections.Specialized;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace PiAgent.Platform.Logging;

public sealed class PiJsonConsoleFormatterOptions : ConsoleFormatterOptions
{
    public string ServiceName { get; set; } = string.Empty;
}

/// <summary>
/// Structured JSON log format: exactly the fields timestamp, level, service,
/// message, correlationId, repositoryId, workflowId, taskId, runId, eventId — absent identifiers
/// are null, never omitted. Secrets never enter logs: only allow-listed state keys (source, action)
/// are emitted from the log state; all other state/scopes are dropped.
/// </summary>
public sealed class CorrelationJsonConsoleFormatter(IOptions<PiJsonConsoleFormatterOptions> options)
    : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "pi-json";

    private static readonly Dictionary<string, Func<CorrelationContext, string?>> CorrelationGetters =
        new()
        {
            ["correlationId"] = c => c.CorrelationId,
            ["repositoryId"] = c => c.RepositoryId,
            ["workflowId"] = c => c.WorkflowId,
            ["taskId"] = c => c.TaskId,
            ["runId"] = c => c.RunId,
            ["eventId"] = c => c.EventId,
        };

    private static readonly string[] AllowedStateKeys = ["source", "action"];

    private readonly PiJsonConsoleFormatterOptions _options = options.Value;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var timestamp = _options.UseUtcTimestamp ? DateTimeOffset.UtcNow : DateTimeOffset.Now;
        var record = new OrderedDictionary
        {
            ["timestamp"] = timestamp.ToString(_options.TimestampFormat, CultureInfo.InvariantCulture),
            ["level"] = logEntry.LogLevel.ToString(),
            ["service"] = _options.ServiceName,
            ["message"] = logEntry.Formatter(logEntry.State, logEntry.Exception),
        };

        var correlation = CorrelationAccessor.Current;
        foreach (var (field, getter) in CorrelationGetters)
            record[field] = correlation is null ? null : getter(correlation);

        if (logEntry.State is IEnumerable<KeyValuePair<string, object?>> pairs)
            foreach (var (key, value) in pairs)
                if (AllowedStateKeys.Contains(key))
                    record[key] = value;

        if (logEntry.Exception is not null)
        {
            // Exception bodies can carry secrets; emit type + message only.
            record["exceptionType"] = logEntry.Exception.GetType().FullName;
            record["exceptionMessage"] = logEntry.Exception.Message;
        }

        textWriter.Write(JsonSerializer.Serialize(record, JsonOptions));
        textWriter.WriteLine();
    }
}
