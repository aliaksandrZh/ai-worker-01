using System.Text.RegularExpressions;

namespace PiAgent.Orchestrator.Config;

/// <summary>
/// Strict unit-string parsing for configuration knob sizes: `256KiB`, `50MiB`, `2GiB`.
/// (Durations live in Platform's SystemYaml.ParseDurationValue — single grammar for the whole plane.)
/// Bad values throw ArgumentException naming the offending value.
/// </summary>
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
