using TinyHarness.Cli;

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
}
