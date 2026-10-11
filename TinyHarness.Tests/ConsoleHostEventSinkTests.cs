using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     run 命令事件展示契约：增量流式内联打印、跨增量密钥脱敏、控制字符转义、工具单行
///     生命周期、非增量事件前补换行、终态事件收尾。
///     The run command's event display contract: deltas stream inline, secrets spanning
///     deltas are redacted, control characters are escaped, tools render as one line,
///     line events close an open streamed line, and the terminal event tidies up.
/// </summary>
public class ConsoleHostEventSinkTests
{
    private static HostEvent TextDelta(string delta)
    {
        return new HostEvent { Kind = HostEventKind.AssistantTextDelta, Sequence = 1, Payload = delta };
    }

    private static async Task<string> RenderAsync(params HostEvent[] events)
    {
        using var output = new StringWriter { NewLine = "\n" };
        var sink = new ConsoleHostEventSink(output : output);
        foreach (var @event in events) await sink.PublishAsync(@event, CancellationToken.None);

        return output.ToString();
    }

    private static HostEvent ToolStartedEvent(string summary = "read_file src/a.cs")
    {
        return new HostEvent
        {
            Kind     = HostEventKind.ToolStarted,
            Sequence = 1,
            Payload  = new ToolPreparation
            {
                ToolName   = "read_file",
                CallId     = "c1",
                Arguments  = new System.Text.Json.Nodes.JsonObject(),
                Capability = "filesystem.read",
                Summary    = summary
            }
        };
    }

    private static HostEvent ToolFinishedEvent(bool succeeded = true, int? exitCode = null, bool timedOut = false,
                                               bool truncated = false)
    {
        return new HostEvent
        {
            Kind     = HostEventKind.ToolFinished,
            Sequence = 2,
            Payload  = new ToolExecutionOutcome(
                new ToolPreparation
                {
                    ToolName   = "read_file",
                    CallId     = "c1",
                    Arguments  = new System.Text.Json.Nodes.JsonObject(),
                    Capability = "filesystem.read",
                    Summary    = "read_file src/a.cs"
                },
                new ToolResult
                {
                    Succeeded       = succeeded,
                    Content         = string.Empty,
                    ExitCode        = exitCode,
                    TimedOut        = timedOut,
                    OutputTruncated = truncated
                })
        };
    }

    [Fact]
    public async Task Deltas_StreamInlineWithoutTrailingNewline()
    {
        var output = await RenderAsync(TextDelta("Hel"), TextDelta("lo"));

        Assert.Equal("Hello", output);
    }

    [Fact]
    public async Task LineEvent_AfterOpenDelta_ClosesTheLineFirst()
    {
        var usage = new HostEvent
        {
            Kind     = HostEventKind.ModelUsage,
            Sequence = 2,
            Payload  = new ModelUsage(120, 30, "stop", false)
        };

        var output = await RenderAsync(TextDelta("partial answer"), usage);

        Assert.Equal("partial answer\nusage: 120 in / 30 out tokens\n", output);
    }

    [Fact]
    public async Task TerminalEvent_ClosesTrailingOpenLine()
    {
        var finished = new HostEvent
        {
            Kind     = HostEventKind.RunFinished,
            Sequence = 2,
            Payload  = new AgentResult { Status = AgentStatus.Completed }
        };

        var output = await RenderAsync(TextDelta("still open"), finished);

        Assert.Equal("still open\n", output);
    }

    [Fact]
    public async Task ToolLifecycle_RendersOneLineWithTerminalSuffix()
    {
        var output = await RenderAsync(ToolStartedEvent(), ToolFinishedEvent(exitCode : 0));

        Assert.Equal("tool: read_file src/a.cs -> ok (exit 0)\n", output);
    }

    [Fact]
    public async Task ToolFailure_TimeoutAndTruncation_AreReflected()
    {
        var output = await RenderAsync(ToolStartedEvent(), ToolFinishedEvent(succeeded : false, timedOut : true));

        Assert.Equal("tool: read_file src/a.cs -> timed out\n", output);

        var truncated = await RenderAsync(ToolStartedEvent(),
                                          ToolFinishedEvent(succeeded : false, exitCode : 1, truncated : true));
        Assert.Equal("tool: read_file src/a.cs -> failed (exit 1) (output truncated)\n", truncated);
    }

    [Fact]
    public async Task InteractiveApprovals_PrintButRuleDecisionsStayQuiet()
    {
        using var output = new StringWriter { NewLine = "\n" };
        var sink = new ConsoleHostEventSink(output : output);
        await sink.PublishAsync(new HostEvent
                                {
                                    Kind     = HostEventKind.ApprovalResolved,
                                    Sequence = 1,
                                    Payload  = new ApprovalResolution(PermissionDecision.Allow, null, "allow")
                                },
                                CancellationToken.None);
        await sink.PublishAsync(new HostEvent
                                {
                                    Kind     = HostEventKind.ApprovalResolved,
                                    Sequence = 2,
                                    Payload  = new ApprovalResolution(PermissionDecision.Ask, ApprovalAction.AllowOnce,
                                                                      "allow once")
                                },
                                CancellationToken.None);

        Assert.Equal("approval: allow once\n", output.ToString());
    }

    [Fact]
    public async Task ApprovalRequest_ClosesTheOpenStreamedLineWithoutPrinting()
    {
        // The approval provider draws its own prompt; the sink only retires the streamed
        // line so the prompt never merges into the answer text.
        using var output = new StringWriter { NewLine = "\n" };
        var sink = new ConsoleHostEventSink(output : output);
        await sink.PublishAsync(TextDelta("working on it"), CancellationToken.None);
        await sink.PublishAsync(new HostEvent { Kind = HostEventKind.ApprovalRequested, Sequence = 2 },
                                CancellationToken.None);

        Assert.Equal("working on it\n", output.ToString());
    }

    [Fact]
    public async Task ToolFinished_AfterLineClosed_RewritesTheWholeLine()
    {
        var usage = new HostEvent
        {
            Kind     = HostEventKind.ModelUsage,
            Sequence = 2,
            Payload  = new ModelUsage(120, 30, "stop", false)
        };

        var output = await RenderAsync(ToolStartedEvent(), usage, ToolFinishedEvent(exitCode : 0));

        Assert.Equal("tool: read_file src/a.cs\n" +
                     "usage: 120 in / 30 out tokens\n" +
                     "tool: read_file src/a.cs -> ok (exit 0)\n",
                     output);
    }

    [Fact]
    public async Task Deltas_AreRedactedAcrossFragments_BeforeDisplay()
    {
        using var output = new StringWriter { NewLine = "\n" };
        var sink = new ConsoleHostEventSink(new Dictionary<string, string> { ["env"] = "sk-secret-value" },
                                            output);
        await sink.PublishAsync(TextDelta("see sk-secret-"), CancellationToken.None);
        await sink.PublishAsync(TextDelta("value now"), CancellationToken.None);
        await sink.PublishAsync(new HostEvent { Kind = HostEventKind.RunFinished, Sequence = 3 },
                                CancellationToken.None);

        Assert.Equal("see [REDACTED] now\n", output.ToString());
    }

    [Fact]
    public async Task Deltas_SecretSplitAcrossStreams_StayRedactedAcrossLineEvents()
    {
        // The usage line retires the streamed line between the two model streams; the
        // held-back "sk-secret-" prefix must not surface there raw, or the completion in
        // the next stream would finish the secret on screen.
        using var output = new StringWriter { NewLine = "\n" };
        var sink = new ConsoleHostEventSink(new Dictionary<string, string> { ["env"] = "sk-secret-value" },
                                            output);
        await sink.PublishAsync(TextDelta("see sk-secret-"), CancellationToken.None);
        await sink.PublishAsync(new HostEvent
                                {
                                    Kind     = HostEventKind.ModelUsage,
                                    Sequence = 2,
                                    Payload  = new ModelUsage(1, 1, "stop", false)
                                },
                                CancellationToken.None);
        await sink.PublishAsync(TextDelta("value now"), CancellationToken.None);
        await sink.PublishAsync(new HostEvent { Kind = HostEventKind.RunFinished, Sequence = 3 },
                                CancellationToken.None);

        var shown = output.ToString();
        Assert.Equal("see \nusage: 1 in / 1 out tokens\n[REDACTED] now\n", shown);
        Assert.DoesNotContain("sk-secret-value", shown);
    }

    [Fact]
    public async Task ErrorLines_PrintRedacted()
    {
        using var output = new StringWriter { NewLine = "\n" };
        var sink = new ConsoleHostEventSink(new Dictionary<string, string> { ["env"] = "tok123" }, output);
        await sink.PublishAsync(new HostEvent
                                {
                                    Kind     = HostEventKind.RunError,
                                    Sequence = 1,
                                    Payload  = "request with tok123 failed"
                                },
                                CancellationToken.None);

        Assert.Equal("error: request with [REDACTED] failed\n", output.ToString());
    }

    [Fact]
    public async Task ControlCharacters_InDeltas_AreEscaped()
    {
        var output = await RenderAsync(TextDelta("a\u001b[2Jb"));

        Assert.Equal("a\\u001B[2Jb", output);
    }

    [Fact]
    public async Task Compaction_KeepsTheEstablishedLineFormat()
    {
        var change = new HostEvent
        {
            Kind     = HostEventKind.CompactionApplied,
            Sequence = 1,
            Payload  = new ContextChange(12345, 8000)
        };

        var output = await RenderAsync(change);

        Assert.Equal("Context: 12,345 -> 8,000 tokens after compaction\n", output);
    }
}
