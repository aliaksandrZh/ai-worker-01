using System.Text.RegularExpressions;
using PiAgent.Infrastructure.Persistence;

namespace PiAgent.Orchestrator.Commands;

/// <summary>Grammar rejections distinguishable at parse time; persistence/status decisions and
/// the policy checks (repository pause, permitted commands, target types, allowlist) live in the
/// comment-command processor.</summary>
public enum CommandRejection
{
    UnknownCommand,
    AmbiguousCommand,
    MalformedCommand,
    CommandInQuoteOrCodeBlock,
}

public abstract record CommandParseResult;

/// <summary>Command token mapped to its canonical `AgentCommandNames` value; trailing text after the
/// command token on the first line is user context (kept verbatim; `arguments` stays `{}`).</summary>
public sealed record CommandParsed(string Name, string TrailingContext) : CommandParseResult;

/// <summary>The comment contains an actual directive but it cannot be processed; `Detail` is
/// diagnostics (raw token for unknown commands).</summary>
public sealed record CommandRejectedParse(CommandRejection Reason, string Detail) : CommandParseResult;

/// <summary>No directive-shaped text the grammar can act on — mention-only comments are context only.</summary>
public sealed record NoCommandDetected : CommandParseResult;

/// <summary>
/// The command-grammar parser (approved design 2026-11-20):
/// directives inside blockquotes, fenced/indented code blocks, and inline code are not usable
/// directives — a stripped occurrence in a comment addressing the bot (case-sensitive login anywhere,
/// word-bounded) is rejected `command_in_quote_or_code_block` so the user is told to post the command
/// as plain text; without the login it stays context. Exactly one usable directive
/// on the first non-empty line (only whitespace before the mention, ≥1 space before the slash token)
/// parses; any other directive-shaped text is malformed; two usable directives are ambiguous.
/// </summary>
public static partial class CommandParser
{
    /// <summary>Slash token → canonical command name (case-insensitive keys).</summary>
    private static readonly Dictionary<string, string> KnownCommands = new(StringComparer.Ordinal)
    {
        ["plan"] = AgentCommandNames.Plan,
        ["code"] = AgentCommandNames.Code,
        ["review"] = AgentCommandNames.Review,
        ["test-cases"] = AgentCommandNames.TestCases,
        ["status"] = AgentCommandNames.Status,
        ["stop"] = AgentCommandNames.Stop,
        ["help"] = AgentCommandNames.Help,
        ["retry"] = AgentCommandNames.Retry,
    };

    /// <summary>Opening/closing fence marker in the first 4 columns (GitHub: ≤3 indent spaces).</summary>
    [GeneratedRegex(@"^ {0,3}(?:```+|~~~+)")]
    private static partial Regex FenceMarkerRegex();

    /// <summary>Inline-code spans masked out (length-preserving) before usable matching.</summary>
    [GeneratedRegex("`[^`]*`")]
    private static partial Regex InlineCodeSpanRegex();

    /// <summary>Indented code block: a tab or ≥4 leading spaces (per line state machine).</summary>
    [GeneratedRegex(@"^(?:\t| {4,})")]
    private static partial Regex IndentedCodeRegex();

    /// <summary>Blockquote prefix: ≤3 indent spaces then one or more `> ` markers.</summary>
    [GeneratedRegex(@"^ {0,3}(?:>[ ]?)+")]
    private static partial Regex BlockquoteRegex();

    /// <summary>The directive shape: word boundary, exact-case mention, whitespace, slash token.
    /// Login must be preceded by line start or whitespace and followed by whitespace before `/token`
    /// (`@login2` is a different user; `@login/plan` glued is not a directive).</summary>
    private static readonly Dictionary<string, Regex> ShapeCache = new(StringComparer.Ordinal);

    private static Regex DirectiveShape(string botLogin)
    {
        if (ShapeCache.TryGetValue(botLogin, out var cached))
            return cached;
        var regex = new Regex($@"(?<=^|\s)@{Regex.Escape(botLogin)}[ \t]+/([A-Za-z0-9_-]+)", RegexOptions.None, TimeSpan.FromSeconds(1));
        ShapeCache[botLogin] = regex;
        return regex;
    }

    /// <summary>A stripped occurrence also covers a bare `/token` inside quote/code content — the
    /// mention may sit outside the code form (the directive is ignored as a unit:
    /// mention present anywhere + coded command → rejected). Preceded by start, whitespace, or `>`.</summary>
    [GeneratedRegex(@"(?<=^|[\s>])/([A-Za-z0-9_-]+)")]
    private static partial Regex BareTokenRegex();

    /// <summary>The bot login addressed anywhere in the body, word-bounded (`@login2` never matches
    /// login `login`; the login typed inside a quote/code still counts as "addressed".</summary>
    private static readonly Dictionary<string, Regex> MentionCache = new(StringComparer.Ordinal);

    private static Regex MentionAnywhere(string botLogin)
    {
        if (MentionCache.TryGetValue(botLogin, out var cached))
            return cached;
        var regex = new Regex($@"(^|[^A-Za-z0-9])@{Regex.Escape(botLogin)}([^A-Za-z0-9]|$)", RegexOptions.None, TimeSpan.FromSeconds(1));
        MentionCache[botLogin] = regex;
        return regex;
    }

    public static CommandParseResult Parse(string body, string botLogin)
    {
        if (string.IsNullOrEmpty(body))
            return new NoCommandDetected();

        var shape = DirectiveShape(botLogin);
        var lines = body.Replace("\r\n", "\n").Split('\n');
        var usable = new List<(int LineIndex, Match Match)>();
        bool strippedDirective = false;
        int firstNonEmptyIndex = -1;
        bool inFence = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            if (firstNonEmptyIndex < 0 && raw.Trim().Length > 0)
                firstNonEmptyIndex = i;

            if (FenceMarkerRegex().IsMatch(raw))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence)
            {
                strippedDirective |= ContainsShape(raw, shape) || BareTokenRegex().IsMatch(raw);
                continue;
            }

            var masked = InlineCodeSpanRegex().Replace(raw, static m => new string(' ', m.Length));

            if (IndentedCodeRegex().IsMatch(masked))
            {
                strippedDirective |= ContainsShape(masked, shape) || SpansContainShape(raw, shape) || BareTokenRegex().IsMatch(masked);
                continue;
            }

            var quote = BlockquoteRegex().Match(masked);
            if (quote.Success)
            {
                var content = masked[quote.Length..];
                strippedDirective |= ContainsShape(content, shape) || SpansContainShape(content, shape) || BareTokenRegex().IsMatch(content);
                continue;
            }

            var matchCount = usable.Count;
            foreach (Match match in shape.Matches(masked))
                usable.Add((i, match));

            if (usable.Count == matchCount)
                strippedDirective |= SpansContainShape(raw, shape);
        }

        if (usable.Count > 1)
            return new CommandRejectedParse(CommandRejection.AmbiguousCommand, $"{usable.Count} command directives in one comment");

        if (usable.Count == 1)
        {
            var (lineIndex, match) = usable[0];
            var line = lines[lineIndex];

            if (lineIndex != firstNonEmptyIndex)
                return new CommandRejectedParse(CommandRejection.MalformedCommand,
                    "the command must be on the first non-empty line of the comment");

            var beforeMention = line[..match.Index];
            if (beforeMention.Length > 0 && !beforeMention.All(char.IsWhiteSpace))
                return new CommandRejectedParse(CommandRejection.MalformedCommand,
                    "only whitespace may precede the bot mention on the command line");

            var token = match.Groups[1].Value;
            var tokenLower = token.ToLowerInvariant();
            if (!KnownCommands.TryGetValue(tokenLower, out var name))
                return new CommandRejectedParse(CommandRejection.UnknownCommand, token);

            return new CommandParsed(name, line[(match.Index + match.Length)..]);
        }

        if (strippedDirective && MentionAnywhere(botLogin).IsMatch(body))
            return new CommandRejectedParse(CommandRejection.CommandInQuoteOrCodeBlock,
                "the command directive was inside a blockquote, code block, or inline code; post it as plain text");

        return new NoCommandDetected();
    }

    private static bool ContainsShape(string text, Regex shape) => shape.IsMatch(text);

    /// <summary>Inline-code span contents are stripped regions too: a token typed fully inside
    /// backticks counts as a stripped occurrence.</summary>
    private static bool SpansContainShape(string text, Regex shape) =>
        InlineCodeSpanRegex().Matches(text).Any(m => shape.IsMatch(m.Value[1..^1]) || BareTokenRegex().IsMatch(m.Value[1..^1]));
}
