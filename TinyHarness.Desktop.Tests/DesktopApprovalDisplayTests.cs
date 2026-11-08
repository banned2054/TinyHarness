using System.Text.Json.Nodes;
using NUnit.Framework;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Desktop.Runtime;

namespace TinyHarness.Desktop.Tests;

/// <summary>
///     <para>
///         Desktop 审批卡展示事实行（B6-4）：DesktopApprovalProvider 把 ApprovalRequest.DisplayNotes
///         逐行脱敏映射进 DesktopApproval（与 CLI fact 行同一单一来源）；DisplayNotes 为可选参数，
///         旧位置构造兼容（缺省 null）。
///     </para>
///     <para>
///         The Desktop approval card's display fact lines (B6-4): DesktopApprovalProvider maps
///         ApprovalRequest.DisplayNotes line by line through redaction into DesktopApproval (the same
///         single source as the CLI fact lines); DisplayNotes is optional and the old positional constructor
///         stays compatible (defaulting to null).
///     </para>
/// </summary>
[TestFixture]
public sealed class DesktopApprovalDisplayTests
{
    [Test]
    public async Task Provider_MapsDisplayNotesIntoDesktopApproval_WithRedaction()
    {
        var notes = new[]
        {
            "backend: WindowsSandbox — same sandbox account identity, non-administrator",
            "file permissions: broad read baseline (not limited to the workspace); " +
            "requested write roots: C:\\outside\\root"
        };
        var preparation = new ToolPreparation
        {
            ToolName    = "shell",
            CallId      = "call_1",
            Arguments   = new JsonObject { ["mode"] = "direct" },
            Capability  = "process.execute",
            Summary     = "secret summary <key>",
            SessionConstraint = "cmd",
            TargetPaths = [@"C:\outside\root"]
        };
        var request = new ApprovalRequest(preparation, PermissionLevel.Level2,
                                          [GrantScope.SingleOperation], null, null, notes);
        DesktopApproval? published = null;
        var provider = new DesktopApprovalProvider(approval =>
                                                   {
                                                       published = approval;
                                                       return ValueTask.CompletedTask;
                                                   },
                                                   text => text.Replace("<key>", "[redacted]"));

        var waiting = provider.PromptAsync(request, CancellationToken.None);
        for (var i = 0; published is null && i < 500; i++)
            await Task.Delay(10, CancellationToken.None);
        Assert.That(published, Is.Not.Null, "审批卡必须在超时前发布。");
        // 裁决后桥接层会回调 null 撤销等待：先快照待决审批再断言。
        var shown = published!;
        provider.Respond(shown.Id, ApprovalResolution.Denied("denied by test"));
        await waiting.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(shown.DisplayNotes, Is.Not.Null);
            Assert.That(shown.DisplayNotes, Is.EqualTo(new[]
            {
                "backend: WindowsSandbox — same sandbox account identity, non-administrator",
                "file permissions: broad read baseline (not limited to the workspace); " +
                "requested write roots: C:\\outside\\root"
            }));
            Assert.That(shown.Summary, Does.Contain("[redacted]"),
                        "桥接层脱敏与 DisplayNotes 同一函数。");
            Assert.That(shown.Summary, Does.Not.Contain("<key>"));
        });
    }

    [Test]
    public void DesktopApproval_OldPositionalConstructor_StaysCompatibleWithoutDisplayNotes()
    {
        // 5 参位置构造（ReviewerNote/Review 之后 DisplayNotes 缺省）。
        DesktopApproval approval = new("id", "shell", "summary", "{}", ["C:\\t"], null,
                                       PermissionLevel.Level2, [GrantScope.SingleOperation]);

        Assert.Multiple(() =>
        {
            Assert.That(approval.ReviewerNote, Is.Null);
            Assert.That(approval.Review, Is.Null);
            Assert.That(approval.DisplayNotes, Is.Null);
        });
    }
}
