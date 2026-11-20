using System.Text.RegularExpressions;

namespace PiAgent.Poller;

/// <summary>§16 polling knobs from config/system.yaml (decision 53 defaults): `poll_interval` 60 s per
/// repository, `poll_overlap` 5-minute window for timestamp queries, `per_page` 100. Missing key →
/// default; missing file or malformed/non-positive value → startup failure. Duration strings parse like
/// the Outbox Worker's config (`60s`, `5m`, `4h`) — a local copy because hosts must not reference hosts.</summary>
public record PollerOptions(TimeSpan Interval, TimeSpan Overlap, int PerPage)
{
    public static readonly PollerOptions Defaults = new(TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(5), 100);
}

public static class PollerConfig
{
    private static readonly Regex DurationPattern = new(@"^(\d+)(s|m|h)$", RegexOptions.Compiled);

    public static PollerOptions Read(string configRoot)
    {
        var path = Path.Combine(configRoot, "system.yaml");
        if (!File.Exists(path))
            throw new PollerConfigException(
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
            throw new PollerConfigException(
                $"configuration error: '{path}' is not valid YAML ({exception.Message})");
        }

        return new PollerOptions(
            Duration("poll_interval", dto.PollInterval, PollerOptions.Defaults.Interval),
            Duration("poll_overlap", dto.PollOverlap, PollerOptions.Defaults.Overlap),
            PerPage(dto.PerPage));

        static TimeSpan Duration(string knob, string? value, TimeSpan fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;
            var match = DurationPattern.Match(value.Trim());
            if (!match.Success)
                throw new PollerConfigException(
                    $"configuration error: {knob} '{value}' must be an integer followed by s, m, or h (e.g. 60s, 5m, 4h)");
            var amount = long.Parse(match.Groups[1].Value);
            var parsed = match.Groups[2].Value switch
            {
                "s" => TimeSpan.FromSeconds(amount),
                "m" => TimeSpan.FromMinutes(amount),
                _ => TimeSpan.FromHours(amount),
            };
            if (parsed <= TimeSpan.Zero)
                throw new PollerConfigException($"configuration error: {knob} '{value}' must be positive");
            return parsed;
        }

        static int PerPage(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return PollerOptions.Defaults.PerPage;
            if (!int.TryParse(value.Trim(), out var parsed) || parsed <= 0)
                throw new PollerConfigException($"configuration error: per_page '{value}' must be a positive integer");
            return parsed;
        }
    }

    private sealed class YamlDto
    {
        [YamlDotNet.Serialization.YamlMember(Alias = "poll_interval")] public string? PollInterval { get; set; }
        [YamlDotNet.Serialization.YamlMember(Alias = "poll_overlap")] public string? PollOverlap { get; set; }
        [YamlDotNet.Serialization.YamlMember(Alias = "per_page")] public string? PerPage { get; set; }
    }
}
