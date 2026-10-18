using System.Text.Json.Nodes;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         run 命令事件展示契约：增量流式内联打印、跨增量密钥脱敏、控制字符转义、工具单行
///         生命周期、非增量事件前补换行、终态事件收尾。
///     </para>
///     <para>
///         The run command's event display contract: deltas stream inline, secrets spanning
///         deltas are redacted, control characters are escaped, tools render as one line,
///         line events close an open streamed line, and the terminal event tidies up.
///     </para>
/// </summary>
public class ConsoleHostEventSinkTests
{
    [Test]
    public async Task ProjectInstructionNoticeDisplaysOnlyRedactedSourceMetadata()
    {
        var output = new StringWriter();
        var sink   = new ConsoleHostEventSink(new Dictionary<string, string> { ["test"] = "synthetic-secret" }, output);
        await sink.PublishAsync(new HostEvent
        {
            Kind = HostEventKind.ProjectInstructionsChanged,
            Payload = new ProjectInstructionsChanged("rules-applied", "revision", null, "changed", 1, 2,
                                                     "discovery", "hash", "tinyharness.project-instructions-renderer/1",
                                                     ["src/synthetic-secret/AGENTS.md (scope: src/synthetic-secret)"])
        }, CancellationToken.None);
        Assert.That(output.ToString(), Does.Contain("revision revision").And.Contain("AGENTS.md"));
        Assert.That(output.ToString(), Does.Not.Contain("synthetic-secret"));
    }

    private static HostEvent TextDelta(string delta) => new()
        { Kind = HostEventKind.AssistantTextDelta, Sequence = 1, Payload = delta };

    private static async Task<string> RenderAsync(params HostEvent[] events)
    {
        using var output = new StringWriter { NewLine = "\n" };
        var       sink   = new ConsoleHostEventSink(output : output);
        foreach (var @event in events) await sink.PublishAsync(@event, CancellationToken.None);

        return output.ToString();
    }

    private static HostEvent ToolStartedEvent(string summary = "read_file src/a.cs") =>
        new()
        {
            Kind     = HostEventKind.ToolStarted,
            Sequence = 1,
            Payload = new ToolPreparation
            {
                ToolName   = "read_file",
                CallId     = "c1",
                Arguments  = new JsonObject(),
                Capability = "filesystem.read",
                Summary    = summary
            }
        };

    private static HostEvent ToolFinishedEvent(
        bool succeeded = true, int? exitCode = null, bool timedOut = false, bool truncated = false) => new()
    {
        Kind     = HostEventKind.ToolFinished,
        Sequence = 2,
        Payload = new ToolExecutionOutcome(new ToolPreparation
                                           {
                                               ToolName   = "read_file",
                                               CallId     = "c1",
                                               Arguments  = new JsonObject(),
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

    [Test]
    public async Task Deltas_StreamInlineWithoutTrailingNewline()
    {
        var output = await RenderAsync(TextDelta("Hel"), TextDelta("lo"));

        Assert.That(output, Is.EqualTo("Hello"));
    }

    [Test]
    public async Task LineEvent_AfterOpenDelta_ClosesTheLineFirst()
    {
        var usage = new HostEvent
        {
            Kind     = HostEventKind.ModelUsage,
            Sequence = 2,
            Payload  = new ModelUsage(120, 30, "stop", false)
        };

        var output = await RenderAsync(TextDelta("partial answer"), usage);

        Assert.That(output, Is.EqualTo("partial answer\nusage: 120 in / 30 out tokens\n"));
    }

    [Test]
    public async Task TerminalEvent_ClosesTrailingOpenLine()
    {
        var finished = new HostEvent
        {
            Kind     = HostEventKind.RunFinished,
            Sequence = 2,
            Payload  = new AgentResult { Status = AgentStatus.Completed }
        };

        var output = await RenderAsync(TextDelta("still open"), finished);

        Assert.That(output, Is.EqualTo("still open\n"));
    }

    [Test]
    public async Task ToolLifecycle_RendersOneLineWithTerminalSuffix()
    {
        var output = await RenderAsync(ToolStartedEvent(), ToolFinishedEvent(exitCode : 0));

        Assert.That(output, Is.EqualTo("tool: read_file src/a.cs -> ok (exit 0)\n"));
    }

    [Test]
    public async Task ToolFailure_TimeoutAndTruncation_AreReflected()
    {
        var output = await RenderAsync(ToolStartedEvent(), ToolFinishedEvent(false, timedOut : true));

        Assert.That(output, Is.EqualTo("tool: read_file src/a.cs -> timed out\n"));

        var truncated = await RenderAsync(ToolStartedEvent(),
                                          ToolFinishedEvent(false, 1, truncated : true));
        Assert.That(truncated, Is.EqualTo("tool: read_file src/a.cs -> failed (exit 1) (output truncated)\n"));
    }

    [Test]
    public async Task InteractiveApprovals_PrintButRuleDecisionsStayQuiet()
    {
        using var output = new StringWriter { NewLine = "\n" };
        var       sink   = new ConsoleHostEventSink(output : output);
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
                                    Payload = new ApprovalResolution(PermissionDecision.Ask, ApprovalAction.AllowOnce,
                                                                     "allow once")
                                },
                                CancellationToken.None);

        Assert.That(output.ToString(), Is.EqualTo("approval: allow once\n"));
    }

    [Test]
    public async Task ApprovalRequest_ClosesTheOpenStreamedLineWithoutPrinting()
    {
        // 审批提示由审批方自行绘制；sink 只结束当前流式行，避免提示与回答文本拼在一起。
        using var output = new StringWriter { NewLine = "\n" };
        var       sink   = new ConsoleHostEventSink(output : output);
        await sink.PublishAsync(TextDelta("working on it"), CancellationToken.None);
        await sink.PublishAsync(new HostEvent { Kind = HostEventKind.ApprovalRequested, Sequence = 2 },
                                CancellationToken.None);

        Assert.That(output.ToString(), Is.EqualTo("working on it\n"));
    }

    [Test]
    public async Task ToolFinished_AfterLineClosed_RewritesTheWholeLine()
    {
        var usage = new HostEvent
        {
            Kind     = HostEventKind.ModelUsage,
            Sequence = 2,
            Payload  = new ModelUsage(120, 30, "stop", false)
        };

        var output = await RenderAsync(ToolStartedEvent(), usage, ToolFinishedEvent(exitCode : 0));

        Assert.That(output, Is.EqualTo("tool: read_file src/a.cs\n"      +
                                       "usage: 120 in / 30 out tokens\n" +
                                       "tool: read_file src/a.cs -> ok (exit 0)\n"));
    }

    [Test]
    public async Task Deltas_AreRedactedAcrossFragments_BeforeDisplay()
    {
        using var output = new StringWriter { NewLine = "\n" };
        var sink = new ConsoleHostEventSink(new Dictionary<string, string> { ["env"] = "sk-secret-value" },
                                            output);
        await sink.PublishAsync(TextDelta("see sk-secret-"), CancellationToken.None);
        await sink.PublishAsync(TextDelta("value now"), CancellationToken.None);
        await sink.PublishAsync(new HostEvent { Kind = HostEventKind.RunFinished, Sequence = 3 },
                                CancellationToken.None);

        Assert.That(output.ToString(), Is.EqualTo("see [REDACTED] now\n"));
    }

    [Test]
    public async Task Deltas_SecretSplitAcrossStreams_StayRedactedAcrossLineEvents()
    {
        // usage 行在两段模型流之间结束当前流式行；被扣留的 "sk-secret-" 前缀不得在此原样出现，
        // 否则下一段流的补全会把整个密钥补齐显示出来。
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
        Assert.That(shown, Is.EqualTo("see \nusage: 1 in / 1 out tokens\n[REDACTED] now\n"));
        Assert.That(shown, Does.Not.Contain("sk-secret-value"));
    }

    [Test]
    public async Task ErrorLines_PrintRedacted()
    {
        using var output = new StringWriter { NewLine = "\n" };
        var       sink   = new ConsoleHostEventSink(new Dictionary<string, string> { ["env"] = "tok123" }, output);
        await sink.PublishAsync(new HostEvent
                                {
                                    Kind     = HostEventKind.RunError,
                                    Sequence = 1,
                                    Payload  = "request with tok123 failed"
                                },
                                CancellationToken.None);

        Assert.That(output.ToString(), Is.EqualTo("error: request with [REDACTED] failed\n"));
    }

    [Test]
    public async Task ControlCharacters_InDeltas_AreEscaped()
    {
        var output = await RenderAsync(TextDelta("a\u001b[2Jb"));

        Assert.That(output, Is.EqualTo("a\\u001B[2Jb"));
    }

    [Test]
    public async Task Compaction_KeepsTheEstablishedLineFormat()
    {
        var change = new HostEvent
        {
            Kind     = HostEventKind.CompactionApplied,
            Sequence = 1,
            Payload  = new ContextChange(12345, 8000)
        };

        var output = await RenderAsync(change);

        Assert.That(output, Is.EqualTo("Context: 12,345 -> 8,000 tokens after compaction\n"));
    }
}
