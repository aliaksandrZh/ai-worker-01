using PiAgent.OutboxWorker.GitHub;
using System.Text.RegularExpressions;

namespace PiAgent.OutboxWorker;

/// <summary>Outbox Worker startup configuration errors — abort host startup with a diagnosable message.</summary>
public sealed class OutboxWorkerConfigException(string message) : Exception(message);

/// <summary>
/// The Outbox Worker reads exactly one knob directly from the configuration plane (plan ruling 5):
/// `outbox_retry_window` in config/system.yaml (§16, owner System, default 24 h). Missing key →
/// default; missing file or a malformed/non-positive value → startup failure. Unit strings parse like
/// the Orchestrator's DurationValue (`60s`, `5m`, `4h`) — a local copy because hosts must not reference
/// host projects.
/// </summary>
public static class OutboxWorkerConfig
{
    private static readonly Regex DurationPattern = new(@"^(\d+)(s|m|h)$", RegexOptions.Compiled);

    public static OutboxWorkerOptions Read(string configRoot)
    {
        var path = Path.Combine(configRoot, "system.yaml");
        if (!File.Exists(path))
            throw new OutboxWorkerConfigException(
                $"configuration error: the configuration file '{path}' does not exist (configuration plane, 01 §2)");

        YamlDto dto;
        try
        {
            var yaml = File.ReadAllText(path);
            dto = new YamlDotNet.Serialization.DeserializerBuilder()
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<YamlDto>(yaml)
                ?? new YamlDto();
        }
        catch (YamlDotNet.Core.YamlException exception)
        {
            throw new OutboxWorkerConfigException(
                $"configuration error: '{path}' is not valid YAML ({exception.Message})");
        }

        var window = DefaultWindow(dto.OutboxRetryWindow);
        return new OutboxWorkerOptions(window);

        static TimeSpan DefaultWindow(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return OutboxWorkerOptions.DefaultRetryWindow;

            var match = DurationPattern.Match(value.Trim());
            if (!match.Success)
                throw new OutboxWorkerConfigException(
                    $"configuration error: outbox_retry_window '{value}' must be an integer followed by s, m, or h (e.g. 60s, 5m, 4h)");
            var amount = long.Parse(match.Groups[1].Value);
            var parsed = match.Groups[2].Value switch
            {
                "s" => TimeSpan.FromSeconds(amount),
                "m" => TimeSpan.FromMinutes(amount),
                _ => TimeSpan.FromHours(amount),
            };
            if (parsed <= TimeSpan.Zero)
                throw new OutboxWorkerConfigException(
                    $"configuration error: outbox_retry_window '{value}' must be positive");
            return parsed;
        }
    }

    private sealed class YamlDto
    {
        [YamlDotNet.Serialization.YamlMember(Alias = "outbox_retry_window")]
        public string? OutboxRetryWindow { get; set; }
    }
}
