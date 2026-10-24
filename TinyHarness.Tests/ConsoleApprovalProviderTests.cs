using System.Text.Json.Nodes;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

public class ConsoleApprovalProviderTests
{
    [TestCase("a", ApprovalAction.AllowOnce)]
    [TestCase("s", ApprovalAction.AllowSession)]
    [TestCase("d", ApprovalAction.Deny)]
    [TestCase("", ApprovalAction.Deny)]
    [TestCase("unknown", ApprovalAction.Deny)]
    public async Task Prompt_ShowsDiffAndScopeBeforeReadingDecision(string answer, ApprovalAction expected)
    {
        using var    dir   = new TestTempDir();
        const string patch = "--- a/file.txt\n+++ b/file.txt\n@@ -1 +1 @@\n-old\n+new\n";
        var          tool  = new ApplyPatchTool(dir.Workspace);
        var preparation =
            tool.Prepare(new ChatToolCall("p", "apply_patch", new JsonObject { ["patch"] = patch }.ToJsonString()));
        using var output = new StringWriter();
        using var input = new InspectingReader(answer, () =>
        {
            var shown = output.ToString();
            Assert.That(shown, Does.Contain(patch));
            Assert.That(shown, Does.Contain(Path.Combine(dir.Root, "file.txt")));
            Assert.That(shown, Does.Contain("Run approval ('s') grants 'filesystem.write' for the target paths above"));
            Assert.That(shown, Does.Contain("later changes in this scope may run without asking"));
        });
        var provider = new ConsoleApprovalProvider(input, output);

        var result = await provider.PromptAsync(preparation, CancellationToken.None);

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public async Task Prompt_EscapesTerminalControlSequencesInPatch()
    {
        using var    dir   = new TestTempDir();
        const string patch = "--- /dev/null\n+++ b/file.txt\n@@ -0,0 +1 @@\n+\u001b[2Jhidden\n";
        var preparation = new ApplyPatchTool(dir.Workspace).Prepare(new ChatToolCall("p", "apply_patch",
                                                                             new JsonObject { ["patch"] = patch }
                                                                                .ToJsonString()));
        using var output = new StringWriter();
        using var input  = new StringReader("d");

        await new ConsoleApprovalProvider(input, output).PromptAsync(preparation, CancellationToken.None);

        TestAssert.DoesNotContain("\u001b", output.ToString(), StringComparison.Ordinal);
        Assert.That(output.ToString(), Does.Contain("+\\u001B[2Jhidden"));
    }

    [Test]
    public async Task Prompt_ExplainsThatProcessSessionGrantKeepsCommandConstraint()
    {
        using var dir = new TestTempDir();
        var preparation =
            new ShellTool(dir.Workspace).Prepare(new ChatToolCall("shell", "shell",
                                                                  """{"executable":"dotnet","arguments":["test"]}"""));
        using var output = new StringWriter();
        using var input  = new StringReader("d");

        await new ConsoleApprovalProvider(input, output).PromptAsync(preparation, CancellationToken.None);

        Assert.That(output.ToString(), Does.Contain("this exact executable/argument shape"));
        Assert.That(output.ToString(), Does.Contain("dotnet test"));
    }

    [Test]
    public async Task Prompt_StatesRunScopedApprovalAndLegacySessionInputMeaning()
    {
        using var dir = new TestTempDir();
        var preparation =
            new ApplyPatchTool(dir.Workspace).Prepare(new ChatToolCall("p", "apply_patch",
                                                                       new JsonObject
                                                                       {
                                                                           ["patch"] = "--- a/f.txt\n" +
                                                                               "+++ b/f.txt\n"         +
                                                                               "@@ -1 +1 @@\n-old\n+new\n"
                                                                       }.ToJsonString()));
        using var output = new StringWriter();
        using var input  = new StringReader("d");

        await new ConsoleApprovalProvider(input, output).PromptAsync(preparation, CancellationToken.None);

        // 's' 授权显式限定在本次 run 内（PLAN §4.2）：不延伸到后续 run，保存的会话也不继承。
        // 旧输入 '[s]ession' 仍按同名接受、语义相同。
        var shown = output.ToString();
        Assert.That(shown, Does.Contain("It lasts for the remainder of this run only"));
        Assert.That(shown, Does.Contain("future runs and a saved session do not inherit it"));
        Assert.That(shown, Does.Contain("The legacy '[s]ession' input keeps the same run-scoped meaning"));
    }

    [Test]
    public async Task Prompt_PreservesOriginalLocalPatchPreview()
    {
        using var dir = new TestTempDir();
        var preparation =
            new ApplyPatchTool(dir.Workspace).Prepare(new ChatToolCall("p", "apply_patch",
                                                                       new JsonObject
                                                                       {
                                                                           ["patch"] = "--- a/f.txt\n"    +
                                                                               "+++ b/f.txt\n"            +
                                                                               "@@ -1 +1 @@\n"            +
                                                                               "-key = sk-secret-value\n" +
                                                                               "+key = new\n"
                                                                       }.ToJsonString()));
        using var output = new StringWriter();
        using var input  = new StringReader("d");

        await new ConsoleApprovalProvider(input, output,
                                          new Dictionary<string, string> { ["env"] = "sk-secret-value" })
           .PromptAsync(preparation, CancellationToken.None);

        // 本地审批必须能核对实际 diff，不能用脱敏文本改变补丁内容。
        var shown = output.ToString();
        Assert.That(shown, Does.Contain("-key = sk-secret-value"));
        Assert.That(shown, Does.Not.Contain("[REDACTED]"));
    }

    [Test]
    public async Task Prompt_ShellModeShowsElevatedRiskAndExactSessionScope()
    {
        using var dir   = new TestTempDir();
        var       shell = OperatingSystem.IsWindows() ? "powershell" : "sh";
        var preparation = new ShellTool(dir.Workspace).Prepare(new ChatToolCall("shell", "shell",
                                                                                    new JsonObject
                                                                                    {
                                                                                        ["mode"]  = "shell",
                                                                                        ["shell"] = shell,
                                                                                        ["command"] =
                                                                                            "echo one && echo two"
                                                                                    }.ToJsonString()));
        using var output = new StringWriter();
        using var input  = new StringReader("d");

        await new ConsoleApprovalProvider(input, output).PromptAsync(preparation, CancellationToken.None);

        Assert.That(output.ToString(), Does.Contain("RISK: elevated"));
        Assert.That(output.ToString(), Does.Contain("exact shell type, command text, and working directory"));
        Assert.That(output.ToString(), Does.Contain("echo one && echo two"));
    }

    private sealed class InspectingReader(string answer, Action inspect) : StringReader(answer)
    {
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            inspect();
            return base.ReadLineAsync(cancellationToken);
        }
    }
}
