using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PiAgent.Platform;
using PiAgent.Platform.Logging;
using Xunit;

namespace PiAgent.Platform.Tests.Logging;

public class CorrelationJsonConsoleFormatterTests
{
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.ffffffZ";

    private static string FormatOne(
        string message,
        IReadOnlyList<KeyValuePair<string, object?>>? state = null,
        Exception? exception = null,
        LogLevel level = LogLevel.Information)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PiJsonConsoleFormatterOptions
        {
            ServiceName = "test-service",
            TimestampFormat = TimestampFormat,
            UseUtcTimestamp = true,
        });
        var formatter = new CorrelationJsonConsoleFormatter(options);

        state ??= [];
        using var writer = new StringWriter();
        formatter.Write(
            new LogEntry< IReadOnlyList<KeyValuePair<string, object?>> >(
                level, "PiAgent.Test", default, state, exception, (_, _) => message),
            null,
            writer);
        return writer.ToString();
    }

    [Fact]
    public void EmitsAllTenSpecFields_NullWhenAbsent_UtcSingleLine()
    {
        try
        {
            CorrelationAccessor.Current = new CorrelationContext { TaskId = "t-1" };

            var json = FormatOne("hello");

            Assert.EndsWith("\n", json);
            Assert.DoesNotContain('\n', json.TrimEnd('\n'));
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal(System.Text.Json.JsonValueKind.Object, root.ValueKind);

            string[] allFields =
                ["timestamp", "level", "service", "message", "correlationId", "repositoryId", "workflowId", "taskId", "runId", "eventId"];
            foreach (var field in allFields)
                Assert.True(root.TryGetProperty(field, out _), $"missing field {field}");

            Assert.Equal("t-1", root.GetProperty("taskId").GetString());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("correlationId").ValueKind);
            Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("repositoryId").ValueKind);
            Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("workflowId").ValueKind);
            Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("runId").ValueKind);
            Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("eventId").ValueKind);

            Assert.Equal("test-service", root.GetProperty("service").GetString());
            Assert.Equal("hello", root.GetProperty("message").GetString());

            var timestamp = root.GetProperty("timestamp").GetString();
            Assert.True(
                DateTime.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed),
                $"timestamp not ISO-8601: {timestamp}");
            Assert.Equal(DateTimeKind.Utc, parsed.Kind);
        }
        finally
        {
            CorrelationAccessor.Current = null;
        }
    }

    [Fact]
    public void EmitsAllowListedStateKeys_NeverArbitraryOnes_SecretValuesLeakNowhere()
    {
        try
        {
            CorrelationAccessor.Current = new CorrelationContext { EventId = "e-42" };

            var json = FormatOne(
                "m",
                new List<KeyValuePair<string, object?>>
                {
                    new("source", "unit-test"),
                    new("action", "run"),
                    new("token", "s3cret"),
                });

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            Assert.Equal("unit-test", doc.RootElement.GetProperty("source").GetString());
            Assert.Equal("run", doc.RootElement.GetProperty("action").GetString());
            Assert.Equal("e-42", doc.RootElement.GetProperty("eventId").GetString());
            Assert.DoesNotContain("s3cret", json);
        }
        finally
        {
            CorrelationAccessor.Current = null;
        }
    }
}
