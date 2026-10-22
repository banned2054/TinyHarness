using TinyHarness.Cli.Commands;
using TinyHarness.Cli.Exceptions;
using TinyHarness.Cli.Models;

namespace TinyHarness.Tests;

public class CommandLineTests
{
    [TestCase("chat")]
    [TestCase("run")]
    public void ReasoningDisplay_DefaultsToStatusAndAcceptsBothModes(string command)
    {
        string[] tail = command == "run" ? ["--", "hello"] : [];
        Assert.That(CommandLine.Parse([command, .. tail]).ReasoningDisplay,
                    Is.EqualTo(ReasoningDisplayMode.Status));
        Assert.That(CommandLine.Parse([command, "--reasoning", "status", .. tail]).ReasoningDisplay,
                    Is.EqualTo(ReasoningDisplayMode.Status));
        Assert.That(CommandLine.Parse([command, "--reasoning", "content", .. tail]).ReasoningDisplay,
                    Is.EqualTo(ReasoningDisplayMode.Content));
        TestAssert.Throws<CliUsageException>(() => CommandLine.Parse([command, "--reasoning"]));
        TestAssert.Throws<CliUsageException>(() => CommandLine.Parse([command, "--reasoning", "typo", .. tail]));
        TestAssert.Throws<CliUsageException>(() =>
            CommandLine.Parse([command, "--reasoning", "content", "--reasoning", "status", .. tail]));
    }

    [Test]
    public void ReasoningDisplay_WorksVerblessAndDoesNotConsumePromptAfterDoubleDash()
    {
        var options = CommandLine.Parse(["--reasoning", "content", "--", "--reasoning", "status"]);
        Assert.That(options.ReasoningDisplay, Is.EqualTo(ReasoningDisplayMode.Content));
        Assert.That(options.Prompt, Is.EqualTo("--reasoning status"));
        var resumed = CommandLine.Parse(["chat", "--session", new string('a', 32), "--reasoning", "content"]);
        Assert.That(resumed.ResumeSessionId, Is.EqualTo(new string('a', 32)));
        Assert.That(resumed.ReasoningDisplay, Is.EqualTo(ReasoningDisplayMode.Content));
    }

    [Test]
    public void RootHelpFlag_ReturnsOverviewHelp()
    {
        var options = CommandLine.Parse(["--help"]);

        Assert.That(options.Kind, Is.EqualTo(CliCommandKind.Help));
        Assert.That(options.HelpTopic, Is.Null);
    }

    [Test]
    public void HelpCommand_ParsesTheRequestedTopic()
    {
        var options = CommandLine.Parse(["help", "doctor"]);

        Assert.That(options.Kind, Is.EqualTo(CliCommandKind.Help));
        Assert.That(options.HelpTopic, Is.EqualTo("doctor"));
    }

    [Test]
    public void RunDoubleDash_PreservesCommandLookingPromptText()
    {
        var options = CommandLine.Parse(["run", "--", "init", "notes", "--dry-run"]);

        Assert.That(options.Kind, Is.EqualTo(CliCommandKind.Run));
        Assert.That(options.Prompt, Is.EqualTo("init notes --dry-run"));
    }

    [Test]
    public void RunWithoutPrompt_IsAUsageError()
    {
        var error = TestAssert.Throws<CliUsageException>(() => CommandLine.Parse(["run"]));

        TestAssert.Contains("prompt is required", error.Message, StringComparison.OrdinalIgnoreCase);
        TestAssert.Contains("tinyharness run", error.Usage, StringComparison.Ordinal);
    }

    [Test]
    public void ModelAdd_RequiresAnExplicitContextWindow()
    {
        var error = TestAssert.Throws<CliUsageException>(() => CommandLine.Parse(["model", "add", "model-a"]));

        TestAssert.Contains("--context-window", error.Message, StringComparison.Ordinal);
    }

    [Test]
    public void DoctorConnect_IsParsedWithoutSendingAnyRequest()
    {
        var options = CommandLine.Parse(["doctor", "--connect"]);

        Assert.That(options.Kind, Is.EqualTo(CliCommandKind.Doctor));
        Assert.That(options.Connect, Is.True);
    }

    [Test]
    public void ChatCommand_ParsesWithAnOptionalConfigPath()
    {
        var options = CommandLine.Parse(["chat", "--config", "team.json"]);

        Assert.That(options.Kind, Is.EqualTo(CliCommandKind.Chat));
        Assert.That(options.ConfigPath, Is.EqualTo("team.json"));
    }

    [Test]
    public void ChatCommand_WithoutArguments_StartsTheSession()
    {
        var options = CommandLine.Parse(["chat"]);

        Assert.That(options.Kind, Is.EqualTo(CliCommandKind.Chat));
        Assert.That(options.ConfigPath, Is.Null);
    }

    [Test]
    public void ChatCommand_WithAPromptArgument_IsAUsageError()
    {
        var error = TestAssert.Throws<CliUsageException>(() => CommandLine.Parse(["chat", "explain the repo"]));

        TestAssert.Contains("takes no prompt", error.Message, StringComparison.Ordinal);
        TestAssert.Contains("tinyharness chat", error.Usage, StringComparison.Ordinal);
    }

    [Test]
    public void ChatCommand_HelpFlag_ReturnsTheChatTopic()
    {
        var options = CommandLine.Parse(["chat", "--help"]);

        Assert.That(options.Kind, Is.EqualTo(CliCommandKind.Help));
        Assert.That(options.HelpTopic, Is.EqualTo("chat"));
        Assert.That(HelpText.IsKnownCommand("chat"), Is.True);
        Assert.That(HelpText.For("chat"), Is.EqualTo(HelpText.Chat));
        TestAssert.Contains("tinyharness chat", HelpText.Overview, StringComparison.Ordinal);
    }

    [Test]
    public void ChatCommand_SessionOptionParsesWithAnIdCaseInsensitively()
    {
        var id      = Guid.NewGuid().ToString("N");
        var options = CommandLine.Parse(["chat", "--session", id.ToUpperInvariant(), "--config", "team.json"]);

        Assert.That(options.Kind, Is.EqualTo(CliCommandKind.Chat));
        Assert.That(options.ResumeSessionId, Is.EqualTo(id.ToUpperInvariant()));
        Assert.That(options.ConfigPath, Is.EqualTo("team.json"));
    }

    [Test]
    public void ChatCommand_SessionOptionRequiresAValidIdAtMostOnce()
    {
        var id = Guid.NewGuid().ToString("N");

        var missing = TestAssert.Throws<CliUsageException>(() => CommandLine.Parse(["chat", "--session"]));
        TestAssert.Contains("requires a session id", missing.Message, StringComparison.Ordinal);

        var invalid = TestAssert.Throws<CliUsageException>(() => CommandLine.Parse(["chat", "--session", "../escape"]));
        TestAssert.Contains("not a valid session id", invalid.Message, StringComparison.Ordinal);

        var twice =
            TestAssert.Throws<CliUsageException>(() => CommandLine.Parse(["chat", "--session", id, "--session", id]));
        TestAssert.Contains("at most once", twice.Message, StringComparison.Ordinal);
    }

    [Test]
    public void SessionList_Parses()
    {
        var options = CommandLine.Parse(["session", "list"]);

        Assert.That(options.Kind, Is.EqualTo(CliCommandKind.Session));
        Assert.That(options.Subcommand, Is.EqualTo("list"));
        Assert.That(options.Primary, Is.Null);
    }

    [Test]
    public void SessionShow_ParsesAValidSessionIdOnly()
    {
        var id      = new string('a', 32);
        var options = CommandLine.Parse(["session", "show", id]);

        Assert.That(options.Kind, Is.EqualTo(CliCommandKind.Session));
        Assert.That(options.Subcommand, Is.EqualTo("show"));
        Assert.That(options.Primary, Is.EqualTo(id));

        var escape = TestAssert.Throws<CliUsageException>(() => CommandLine.Parse(["session", "show", "../escape"]));
        TestAssert.Contains("not a valid session id", escape.Message, StringComparison.Ordinal);
        TestAssert.Contains("tinyharness session show", escape.Usage, StringComparison.Ordinal);
    }

    [TestCase("migrate")]
    [TestCase("migrate", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [TestCase("migrate", "../escape")]
    public void SessionMigrate_IsAnUnknownSubcommand(params string[] arguments)
    {
        var error = TestAssert.Throws<CliUsageException>(() => CommandLine.Parse(["session", .. arguments]));
        TestAssert.Contains("Unknown session subcommand", error.Message, StringComparison.Ordinal);
        TestAssert.DoesNotContain("session migrate", HelpText.Session, StringComparison.Ordinal);
    }

    [Test]
    public void Session_WithoutASubcommandIsAUsageError()
    {
        var error = TestAssert.Throws<CliUsageException>(() => CommandLine.Parse(["session"]));

        TestAssert.Contains("session requires a subcommand", error.Message, StringComparison.Ordinal);
        TestAssert.Throws<CliUsageException>(() => CommandLine.Parse(["session", "purge"]));
        TestAssert.Throws<CliUsageException>(() => CommandLine.Parse(["session", "show"]));
    }
}
