using System.Text.Json.Nodes;
using TinyHarness.Cli;
using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Permissions;
using TinyHarness.Core.Tools;

namespace TinyHarness.Tests;

public class ConsoleApprovalProviderTests
{
    [Theory]
    [InlineData("a", ApprovalAction.AllowOnce)]
    [InlineData("s", ApprovalAction.AllowSession)]
    [InlineData("d", ApprovalAction.Deny)]
    [InlineData("", ApprovalAction.Deny)]
    [InlineData("unknown", ApprovalAction.Deny)]
    public async Task Prompt_ShowsDiffAndScopeBeforeReadingDecision(string answer, ApprovalAction expected)
    {
        using var dir = new TestTempDir();
        const string patch = "--- a/file.txt\n+++ b/file.txt\n@@ -1 +1 @@\n-old\n+new\n";
        var tool = new ApplyPatchTool(dir.Workspace);
        var preparation = tool.Prepare(new ChatToolCall("p", "apply_patch",
            new JsonObject { ["patch"] = patch }.ToJsonString()));
        using var output = new StringWriter();
        using var input = new InspectingReader(answer, () =>
        {
            var shown = output.ToString();
            Assert.Contains(patch, shown);
            Assert.Contains(Path.Combine(dir.Root, "file.txt"), shown);
            Assert.Contains("Session approval grants 'filesystem.write' for the target paths above", shown);
            Assert.Contains("later changes in this scope may run without asking", shown);
        });
        var provider = new ConsoleApprovalProvider(input, output);

        var result = await provider.PromptAsync(preparation, CancellationToken.None);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task Prompt_EscapesTerminalControlSequencesInPatch()
    {
        using var dir = new TestTempDir();
        const string patch = "--- /dev/null\n+++ b/file.txt\n@@ -0,0 +1 @@\n+\u001b[2Jhidden\n";
        var preparation = new ApplyPatchTool(dir.Workspace).Prepare(new ChatToolCall("p", "apply_patch",
            new JsonObject { ["patch"] = patch }.ToJsonString()));
        using var output = new StringWriter();
        using var input = new StringReader("d");

        await new ConsoleApprovalProvider(input, output).PromptAsync(preparation, CancellationToken.None);

        Assert.DoesNotContain("\u001b", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("+\\u001B[2Jhidden", output.ToString());
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
