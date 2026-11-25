using System.Text.RegularExpressions;

namespace PiAgent.GithubSync;

/// <summary>
/// The command grammar (simplified port of the approved design, 2026-11-20): directives
/// inside blockquotes, fenced/indented code blocks and inline code are not usable; the
/// bot login (case-sensitive, word-bounded) must appear on the first non-empty line with
/// only whitespace before it and ≥1 space before the slash token.
/// </summary>
public static partial class CommandGrammar
{
    public sealed record Parsed(string Command, string TrailingContext);

    [GeneratedRegex(@"^ {0,3}(?:```+|~~~+)")]
    private static partial Regex FenceMarkerRegex();

    [GeneratedRegex("`[^`]*`")]
    private static partial Regex InlineCodeSpanRegex();

    [GeneratedRegex(@"(?<!\w)")]
    private static partial Regex WordBoundaryPrefixRegex();

    public static Parsed? Parse(string body, string botLogin)
    {
        var masked = InlineCodeSpanRegex().Replace(body, m => new string(' ', m.Length));
        var lines = masked.Split('\n');

        var inFence = false;
        var firstLine = (string?)null;
        var mentionSeen = false;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (FenceMarkerRegex().IsMatch(line))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
                continue;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (firstLine is null)
            {
                firstLine = line;
                mentionSeen = ContainsLogin(line, botLogin);
            }
            else if (!mentionSeen && ContainsLogin(line, botLogin))
            {
                // the login arrived later — the first non-empty line is what matters
                mentionSeen = false;
                break;
            }
        }

        if (firstLine is null || !mentionSeen)
            return null;

        var match = Regex.Match(firstLine, @"^\s*\S+\s+/(\S+)(.*)$");
        if (!match.Success)
            return null;

        var command = match.Groups[1].Value.ToLowerInvariant();
        var trailing = match.Groups[2].Value.Trim();
        return new Parsed(command, trailing);
    }

    private static bool ContainsLogin(string line, string botLogin) =>
        !string.IsNullOrEmpty(botLogin) &&
        Regex.IsMatch(line, $@"(?<!\w){Regex.Escape(botLogin)}(?!\w)");
}
