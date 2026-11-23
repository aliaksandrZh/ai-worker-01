using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace PiAgent.Platform.Config;

/// <summary>
/// Shared mechanics for reading the configuration plane's `system.yaml`: strict load (a
/// missing file or malformed YAML is a diagnosable startup failure) plus the duration / positive-int
/// knob parsers used across the plane (`60s`, `5m`, `4h`). Hosts keep their own narrow DTOs and pick
/// the knobs they read; the Orchestrator's strict ConfigParser remains the schema authority for the
/// whole document. (Previously each host kept a local copy of this mechanics — plan ruling "hosts
/// must not reference host projects" is honored by hosting the primitives in Platform.)
/// </summary>
public static class SystemYaml
{
    private static readonly Regex DurationPattern = new(@"^(\d+)(s|m|h)$", RegexOptions.Compiled);

    /// <summary>Reads `{configRoot}/system.yaml`; a missing file is a diagnosable startup failure.</summary>
    public static string Read(string configRoot)
    {
        var path = Path.Combine(configRoot, "system.yaml");
        if (!File.Exists(path))
            throw new ConfigException(
                $"configuration error: the configuration file '{path}' does not exist");
        return File.ReadAllText(path);
    }

    /// <summary>Deserializes with unknown keys ignored (hosts read narrow knob subsets; the Orchestrator's
    /// strict parser validates the whole document); a malformed document is a diagnosable startup failure.</summary>
    public static T Deserialize<T>(string yaml)
        where T : new()
    {
        try
        {
            return new DeserializerBuilder()
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<T>(yaml) ?? new T();
        }
        catch (YamlDotNet.Core.YamlException exception)
        {
            throw new ConfigException($"configuration error: system.yaml is not valid YAML ({exception.Message})");
        }
    }

    /// <summary>The bare duration grammar of the plane (single copy — formerly repeated as the
    /// Orchestrator's `DurationValue` and the Runtime API's lenient last-char/double parser):
    /// `60s`, `5m`, `4h` — an integer followed by s/m/h. Throws `ArgumentException` so callers can
    /// add file/key context (the Orchestrator's `ParseKnobDuration` does exactly that).</summary>
    public static TimeSpan ParseDurationValue(string value)
    {
        var match = DurationPattern.Match(value.Trim());
        if (!match.Success)
            throw new ArgumentException($"duration '{value}' must be an integer followed by s, m, or h (e.g. 60s, 5m, 4h)");
        var amount = long.Parse(match.Groups[1].Value);
        return match.Groups[2].Value switch
        {
            "s" => TimeSpan.FromSeconds(amount),
            "m" => TimeSpan.FromMinutes(amount),
            _ => TimeSpan.FromHours(amount),
        };
    }

    /// <summary>Duration knob: `60s`, `5m`, `4h` — integer followed by s/m/h, positive; whitespace-only
    /// falls back to the default. Messages name the knob so the failing key is diagnosable.</summary>
    public static TimeSpan Duration(string knob, string? value, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        TimeSpan parsed;
        try
        {
            parsed = ParseDurationValue(value);
        }
        catch (ArgumentException)
        {
            throw new ConfigException(
                $"configuration error: {knob} '{value}' must be an integer followed by s, m, or h (e.g. 60s, 5m, 4h)");
        }
        if (parsed <= TimeSpan.Zero)
            throw new ConfigException($"configuration error: {knob} '{value}' must be positive");
        return parsed;
    }

    /// <summary>Positive-integer knob (e.g. `per_page`); whitespace-only falls back to the default.</summary>
    public static int PositiveInt(string knob, string? value, int fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        if (!int.TryParse(value.Trim(), out var parsed) || parsed <= 0)
            throw new ConfigException($"configuration error: {knob} '{value}' must be a positive integer");
        return parsed;
    }
}
