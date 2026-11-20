using PiAgent.Orchestrator.Commands;
using Xunit;

namespace PiAgent.Hosts.Tests.Commands;

/// <summary>
/// The command grammar (06 §1 + design doc owner decisions): mention + slash command on the first
/// non-empty line, exact case-sensitive bot login, quote/fenced/indented/inline-code directives
/// ignored-or-flaged, exactly one directive per comment, trailing text is user context.
/// </summary>
public class CommandParserTests
{
    private const string Bot = "jsxhuz";

    private static CommandParsed ParseCommand(string body) =>
        Assert.IsType<CommandParsed>(CommandParser.Parse(body, Bot));

    private static CommandRejectedParse ParseRejected(string body) =>
        Assert.IsType<CommandRejectedParse>(CommandParser.Parse(body, Bot));

    private static void ParseNoCommand(string body) =>
        Assert.IsType<NoCommandDetected>(CommandParser.Parse(body, Bot));

    // ---- valid ----

    [Fact]
    public void Valid_FirstLineMentionCommand_WithTrailingContext()
    {
        var result = ParseCommand("@jsxhuz /plan extra text here");
        Assert.Equal("plan", result.Name);
        Assert.Equal(" extra text here", result.TrailingContext);
    }

    [Fact]
    public void Valid_OnlyLeadingWhitespace_Allowed()
    {
        var result = ParseCommand("   @jsxhuz /status");
        Assert.Equal("status", result.Name);
    }

    [Fact]
    public void Valid_BlankLinesBeforeFirstLine()
    {
        var result = ParseCommand("\n\n@jsxhuz /plan");
        Assert.Equal("plan", result.Name);
    }

    [Fact]
    public void Valid_CommandTokenCaseInsensitive()
    {
        var result = ParseCommand("@jsxhuz /PLAN and more");
        Assert.Equal("plan", result.Name);
        Assert.Equal(" and more", result.TrailingContext);
    }

    [Fact]
    public void Valid_TabBetweenMentionAndCommand()
    {
        var result = ParseCommand("@jsxhuz\t/review");
        Assert.Equal("review", result.Name);
    }

    [Fact]
    public void Valid_StrippedDirectiveAlongsideUsableOne_StillParses()
    {
        var result = ParseCommand("@jsxhuz /stop\n> @jsxhuz /stop");
        Assert.Equal("stop", result.Name);
    }

    [Fact]
    public void Valid_TestCasesTokenMapsToCanonicalName()
    {
        var result = ParseCommand("@jsxhuz /test-cases");
        Assert.Equal("test_cases", result.Name);
    }

    // ---- context only ----

    [Fact]
    public void MentionOnlyComment_NoDirective()
    {
        ParseNoCommand("@jsxhuz can you help");
    }

    [Fact]
    public void SlashTokenWithoutMention_NoDirective()
    {
        ParseNoCommand("/plan please");
    }

    [Fact]
    public void NonAdjacentMentionAndCommand_ContextOnly()
    {
        ParseNoCommand("@jsxhuz hello, /plan please");
    }

    [Fact]
    public void DifferentUserMention_ContextOnly()
    {
        ParseNoCommand("@jsxhuz2 /plan");
    }

    [Fact]
    public void WrongCaseBotLogin_ContextOnly()
    {
        ParseNoCommand("@JSXHUZ /plan");
    }

    [Fact]
    public void GluedMentionCommand_NotADirective()
    {
        ParseNoCommand("@jsxhuz/plan");
    }

    [Fact]
    public void FencedDirectiveWithMention_Rejected()
    {
        // the login typed inside the fence still addresses the bot (owner decision Q1)
        var rejection = ParseRejected("```\n@jsxhuz /plan\n```");
        Assert.Equal(CommandRejection.CommandInQuoteOrCodeBlock, rejection.Reason);
    }

    [Fact]
    public void IndentedExampleCommand_NoMentionInBody_ContextOnly()
    {
        // prose quoting a bare command without the bot login stays context
        ParseNoCommand("look at this:\n\n    /plan is an example");
    }

    // ---- rejected by grammar ----

    [Fact]
    public void UnknownCommandToken_Rejected()
    {
        var rejection = ParseRejected("@jsxhuz /frobnicate");
        Assert.Equal(CommandRejection.UnknownCommand, rejection.Reason);
        Assert.Equal("frobnicate", rejection.Detail);
    }

    [Fact]
    public void DirectiveOnLaterLine_Malformed()
    {
        var rejection = ParseRejected("hello all\n\n@jsxhuz /plan");
        Assert.Equal(CommandRejection.MalformedCommand, rejection.Reason);
    }

    [Fact]
    public void TextBeforeMentionOnFirstLine_Malformed()
    {
        var rejection = ParseRejected("hello @jsxhuz /plan");
        Assert.Equal(CommandRejection.MalformedCommand, rejection.Reason);
    }

    [Fact]
    public void TwoDirectives_Ambiguous()
    {
        var rejection = ParseRejected("@jsxhuz /plan\n@jsxhuz /stop");
        Assert.Equal(CommandRejection.AmbiguousCommand, rejection.Reason);
    }

    [Fact]
    public void TwoDirectivesSameCommand_Ambiguous()
    {
        var rejection = ParseRejected("@jsxhuz /plan\n@jsxhuz /plan");
        Assert.Equal(CommandRejection.AmbiguousCommand, rejection.Reason);
    }

    // ---- Q1: bot-addressed comment with quoted/code-blocked directive ----

    [Fact]
    public void FencedDirective_WithMentionAnywhere_Rejected()
    {
        var rejection = ParseRejected("@jsxhuz see below:\n```\n@jsxhuz /plan\n```");
        Assert.Equal(CommandRejection.CommandInQuoteOrCodeBlock, rejection.Reason);
    }

    [Fact]
    public void QuotedDirective_Rejected()
    {
        var rejection = ParseRejected("> @jsxhuz /plan");
        Assert.Equal(CommandRejection.CommandInQuoteOrCodeBlock, rejection.Reason);
    }

    [Fact]
    public void IndentedCodeDirective_WithMention_Rejected()
    {
        var rejection = ParseRejected("    @jsxhuz /plan");
        Assert.Equal(CommandRejection.CommandInQuoteOrCodeBlock, rejection.Reason);
    }

    [Fact]
    public void InlineCodedDirective_WithMention_Rejected()
    {
        var rejection = ParseRejected("@jsxhuz run `/plan` now");
        Assert.Equal(CommandRejection.CommandInQuoteOrCodeBlock, rejection.Reason);
    }

    [Fact]
    public void InlineCodedDirective_NoMentionAnywhere_ContextOnly()
    {
        ParseNoCommand("use `/plan` for planning");
    }

    [Fact]
    public void SlashTokenInFence_NoMentionAnywhere_ContextOnly()
    {
        ParseNoCommand("the syntax is:\n\n    /plan does the thing");
    }

    // ---- input robustness ----

    [Fact]
    public void WindowsLineEndings_Parses()
    {
        var result = ParseCommand("@jsxhuz /plan\r\nsecond line");
        Assert.Equal("plan", result.Name);
        Assert.Equal("", result.TrailingContext);
    }

    [Fact]
    public void MentionAtVeryStartOfBody_Parses()
    {
        var result = ParseCommand("@jsxhuz /help");
        Assert.Equal("help", result.Name);
    }
}
