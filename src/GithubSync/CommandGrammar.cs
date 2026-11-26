using System.Text.RegularExpressions;

namespace PiAgent.GithubSync;

/// <summary>
/// The command grammar (simplified port of the approved design, 2026-11-20, extended to
/// multiple directives per comment): directives inside blockquotes, fenced/indented code
/// blocks and inline code are not usable; every directive LINE carries the bot login
/// (case-sensitive, word-bounded, only whitespace before it) and ≥1 space before the slash
/// token. A comment may hold several directives — one per line (`@bot /approve` then
/// `@bot /code`); each becomes its own command.
/// </summary>
public static partial class CommandGrammar
{
    public sealed record Parsed(string Command, string TrailingContext);

    [GeneratedRegex(@"^ {0,3}(?:```+|~~~+)")]
    private static partial Regex FenceMarkerRegex();

    [GeneratedRegex("`[^`]*`")]
    private static partial Regex InlineCodeSpanRegex();

    [GeneratedRegex(@"^\s*(?<mention>\S+)\s+/(?<command>\S+)(?<trailing>.*)$")]
    private static partial Regex DirectiveRegex();

    /// <summary>Every directive line of the comment, in order. The bot login must appear in
    /// the FIRST non-empty line (the comment is addressed to the bot) — a login arriving
    /// later, on a comment that does not open with it, is not a directive.</summary>
    public static IReadOnlyList<Parsed> ParseAll(string body, string botLogin)
    {
        var masked = InlineCodeSpanRegex().Replace(body, m => new string(' ', m.Length));
        var lines = masked.Split('\n');

        var inFence = false;
        var firstLine = (string?)null;
        var mentionSeen = false;
        var commands = new List<Parsed>();

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
                if (!mentionSeen)
                    break; // the first non-empty line is what matters — not addressed to the bot
            }
            else if (!ContainsLogin(line, botLogin))
            {
                continue; // a non-directive line (e.g. a paragraph under /plan) — not a command
            }

            var match = DirectiveRegex().Match(line);
            if (!match.Success || !match.Groups["mention"].Value.Contains(botLogin, StringComparison.Ordinal))
                continue;

            commands.Add(new Parsed(
                match.Groups["command"].Value.ToLowerInvariant(),
                match.Groups["trailing"].Value.Trim()));
        }

        return commands;
    }

    /// <summary>The first directive (the historical single-command shape).</summary>
    public static Parsed? Parse(string body, string botLogin) =>
        ParseAll(body, botLogin).FirstOrDefault();

    private static bool ContainsLogin(string line, string botLogin) =>
        !string.IsNullOrEmpty(botLogin) &&
        Regex.IsMatch(line, $@"(?<!\w){Regex.Escape(botLogin)}(?!\w)");
}
