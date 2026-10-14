using TinyHarness.Cli.Commands;
using TinyHarness.Cli.Exceptions;
using TinyHarness.Cli.Models;

namespace TinyHarness.Tests;

public class CommandLineTests
{
    [Fact]
    public void RootHelpFlag_ReturnsOverviewHelp()
    {
        var options = CommandLine.Parse(["--help"]);

        Assert.Equal(CliCommandKind.Help, options.Kind);
        Assert.Null(options.HelpTopic);
    }

    [Fact]
    public void HelpCommand_ParsesTheRequestedTopic()
    {
        var options = CommandLine.Parse(["help", "doctor"]);

        Assert.Equal(CliCommandKind.Help, options.Kind);
        Assert.Equal("doctor", options.HelpTopic);
    }

    [Fact]
    public void RunDoubleDash_PreservesCommandLookingPromptText()
    {
        var options = CommandLine.Parse(["run", "--", "init", "notes", "--dry-run"]);

        Assert.Equal(CliCommandKind.Run, options.Kind);
        Assert.Equal("init notes --dry-run", options.Prompt);
    }

    [Fact]
    public void RunWithoutPrompt_IsAUsageError()
    {
        var error = Assert.Throws<CliUsageException>(() => CommandLine.Parse(["run"]));

        Assert.Contains("prompt is required", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tinyharness run", error.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelAdd_RequiresAnExplicitContextWindow()
    {
        var error = Assert.Throws<CliUsageException>(() => CommandLine.Parse(["model", "add", "model-a"]));

        Assert.Contains("--context-window", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorConnect_IsParsedWithoutSendingAnyRequest()
    {
        var options = CommandLine.Parse(["doctor", "--connect"]);

        Assert.Equal(CliCommandKind.Doctor, options.Kind);
        Assert.True(options.Connect);
    }

    [Fact]
    public void ChatCommand_ParsesWithAnOptionalConfigPath()
    {
        var options = CommandLine.Parse(["chat", "--config", "team.json"]);

        Assert.Equal(CliCommandKind.Chat, options.Kind);
        Assert.Equal("team.json", options.ConfigPath);
    }

    [Fact]
    public void ChatCommand_WithoutArguments_StartsTheSession()
    {
        var options = CommandLine.Parse(["chat"]);

        Assert.Equal(CliCommandKind.Chat, options.Kind);
        Assert.Null(options.ConfigPath);
    }

    [Fact]
    public void ChatCommand_WithAPromptArgument_IsAUsageError()
    {
        var error = Assert.Throws<CliUsageException>(() => CommandLine.Parse(["chat", "explain the repo"]));

        Assert.Contains("takes no prompt", error.Message, StringComparison.Ordinal);
        Assert.Contains("tinyharness chat", error.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatCommand_HelpFlag_ReturnsTheChatTopic()
    {
        var options = CommandLine.Parse(["chat", "--help"]);

        Assert.Equal(CliCommandKind.Help, options.Kind);
        Assert.Equal("chat", options.HelpTopic);
        Assert.True(HelpText.IsKnownCommand("chat"));
        Assert.Equal(HelpText.Chat, HelpText.For("chat"));
        Assert.Contains("tinyharness chat", HelpText.Overview, StringComparison.Ordinal);
    }

    [Fact]
    public void ChatCommand_SessionOptionParsesWithAnIdCaseInsensitively()
    {
        var id      = Guid.NewGuid().ToString("N");
        var options = CommandLine.Parse(["chat", "--session", id.ToUpperInvariant(), "--config", "team.json"]);

        Assert.Equal(CliCommandKind.Chat, options.Kind);
        Assert.Equal(id.ToUpperInvariant(), options.ResumeSessionId);
        Assert.Equal("team.json", options.ConfigPath);
    }

    [Fact]
    public void ChatCommand_SessionOptionRequiresAValidIdAtMostOnce()
    {
        var id = Guid.NewGuid().ToString("N");

        var missing = Assert.Throws<CliUsageException>(() => CommandLine.Parse(["chat", "--session"]));
        Assert.Contains("requires a session id", missing.Message, StringComparison.Ordinal);

        var invalid = Assert.Throws<CliUsageException>(() => CommandLine.Parse(["chat", "--session", "../escape"]));
        Assert.Contains("not a valid session id", invalid.Message, StringComparison.Ordinal);

        var twice =
            Assert.Throws<CliUsageException>(() => CommandLine.Parse(["chat", "--session", id, "--session", id]));
        Assert.Contains("at most once", twice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionList_Parses()
    {
        var options = CommandLine.Parse(["session", "list"]);

        Assert.Equal(CliCommandKind.Session, options.Kind);
        Assert.Equal("list", options.Subcommand);
        Assert.Null(options.Primary);
    }

    [Fact]
    public void SessionShow_ParsesAValidSessionIdOnly()
    {
        var id      = new string('a', 32);
        var options = CommandLine.Parse(["session", "show", id]);

        Assert.Equal(CliCommandKind.Session, options.Kind);
        Assert.Equal("show", options.Subcommand);
        Assert.Equal(id, options.Primary);

        var escape = Assert.Throws<CliUsageException>(() => CommandLine.Parse(["session", "show", "../escape"]));
        Assert.Contains("not a valid session id", escape.Message, StringComparison.Ordinal);
        Assert.Contains("tinyharness session show", escape.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void Session_WithoutASubcommandIsAUsageError()
    {
        var error = Assert.Throws<CliUsageException>(() => CommandLine.Parse(["session"]));

        Assert.Contains("session requires a subcommand", error.Message, StringComparison.Ordinal);
        Assert.Throws<CliUsageException>(() => CommandLine.Parse(["session", "purge"]));
        Assert.Throws<CliUsageException>(() => CommandLine.Parse(["session", "show"]));
    }
}
