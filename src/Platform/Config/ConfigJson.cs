using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiAgent.Platform.Config;

/// <summary>
/// Shared mechanics for reading the configuration plane's JSON files: snake_case keys,
/// tolerant of `//` comments and trailing commas (a config file carries its own
/// documentation), unknown properties ignored by default (hosts read narrow subsets;
/// additions to a file never break another module's read). Duration / positive-int knob
/// parsers (`60s`, `5m`, `4h`) live here too — the single copy of the plane's grammar.
/// </summary>
public static class ConfigJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Reads and deserializes a config-plane JSON file; a missing file is `default`.</summary>
    public static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path))
            return null;
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
    }

    /// <summary>Duration knob: `60s`, `5m`, `4h` — integer followed by s/m/h, positive; whitespace-only
    /// falls back to the default. Messages name the knob so the failing key is diagnosable.</summary>
    public static TimeSpan Duration(string knob, string? value, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        var match = System.Text.RegularExpressions.Regex.Match(value.Trim(), @"^(\d+)(s|m|h)$");
        if (!match.Success)
            throw new ConfigException(
                $"configuration error: {knob} '{value}' must be an integer followed by s, m, or h (e.g. 60s, 5m, 4h)");
        var parsed = match.Groups[2].Value switch
        {
            "s" => TimeSpan.FromSeconds(long.Parse(match.Groups[1].Value)),
            "m" => TimeSpan.FromMinutes(long.Parse(match.Groups[1].Value)),
            _ => TimeSpan.FromHours(long.Parse(match.Groups[1].Value)),
        };
        if (parsed <= TimeSpan.Zero)
            throw new ConfigException($"configuration error: {knob} '{value}' must be positive");
        return parsed;
    }

    /// <summary>Positive-integer knob; whitespace-only falls back to the default.</summary>
    public static int PositiveInt(string knob, string? value, int fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        if (!int.TryParse(value.Trim(), out var parsed) || parsed <= 0)
            throw new ConfigException($"configuration error: {knob} '{value}' must be a positive integer");
        return parsed;
    }
}
