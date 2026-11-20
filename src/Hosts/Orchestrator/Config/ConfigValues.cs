using System.Text.RegularExpressions;

namespace PiAgent.Orchestrator.Config;

/// <summary>
/// Strict unit-string parsing for configuration knobs (01 §16 tables). Durations: `60s`, `5m`, `4h`;
/// sizes: `256KiB`, `50MiB`, `2GiB`. Bad values throw ArgumentException naming the offending value.
/// </summary>
public static class DurationValue
{
    private static readonly Regex Pattern = new(@"^(\d+)(s|m|h)$", RegexOptions.Compiled);

    public static TimeSpan Parse(string value)
    {
        var match = Pattern.Match(value.Trim());
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
}

public static class SizeValue
{
    private static readonly Regex Pattern = new(@"^(\d+)(KiB|MiB|GiB)$", RegexOptions.Compiled);

    public static long Parse(string value)
    {
        var match = Pattern.Match(value.Trim());
        if (!match.Success)
            throw new ArgumentException($"size '{value}' must be an integer followed by KiB, MiB, or GiB (e.g. 256KiB, 50MiB)");
        var amount = long.Parse(match.Groups[1].Value);
        return match.Groups[2].Value switch
        {
            "KiB" => amount * 1024,
            "MiB" => amount * 1024 * 1024,
            _ => amount * 1024 * 1024 * 1024,
        };
    }
}
