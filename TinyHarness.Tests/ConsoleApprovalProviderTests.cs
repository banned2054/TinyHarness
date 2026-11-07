using System.Text.Json.Nodes;
using TinyHarness.Cli.Services;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

public class ConsoleApprovalProviderTests
{
    private static ToolPreparation PatchPreparation(TestTempDir dir, string patch) =>
        new ApplyPatchTool(dir.Workspace).Prepare(new ChatToolCall("p", "apply_patch",
                                                                   new JsonObject { ["patch"] = patch }.ToJsonString()));

    private static async Task<ApprovalResolution> PromptAsync(ToolPreparation preparation, string answer,
                                                              StringWriter output, Action? onRead = null)
    {
        TextReader input = onRead is null ? new StringReader(answer) : new InspectingReader(answer, onRead);
        var request = new ApprovalRequest(preparation, PermissionLevel.Level2,
                                          [GrantScope.SingleOperation, GrantScope.Turn, GrantScope.Session],
                                          null, null);
        return await new ConsoleApprovalProvider(input, output).PromptAsync(request, CancellationToken.None);
    }

    [TestCase("a", "allow")]
    [TestCase("y", "allow")]
    [TestCase("t", "turn")]
    [TestCase("s", "session")]
    [TestCase("d", "deny")]
    [TestCase("", "deny")]
    [TestCase("unknown", "deny")]
    public async Task Prompt_MapsAnswersToResolutionsByAllowedScopes(string answer, string expected)
    {
        using var dir   = new TestTempDir();
        var       patch = "--- a/file.txt\n+++ b/file.txt\n@@ -1 +1 @@\n-old\n+new\n";
        var       preparation = PatchPreparation(dir, patch);
        using var output = new StringWriter();

        var result = await PromptAsync(preparation, answer, output);

        Assert.That(Describe(result), Is.EqualTo(expected));
    }

    [Test]
    public async Task Prompt_ShowsDiffScopeAndLevelBeforeReadingDecision()
    {
        using var dir   = new TestTempDir();
        var       patch = "--- a/file.txt\n+++ b/file.txt\n@@ -1 +1 @@\n-old\n+new\n";
        var       preparation = PatchPreparation(dir, patch);
        using var output = new StringWriter();

        await PromptAsync(preparation, "d", output, () =>
        {
            var shown = output.ToString();
            Assert.That(shown, Does.Contain(patch));
            Assert.That(shown, Does.Contain(Path.Combine(dir.Root, "file.txt")));
            Assert.That(shown, Does.Contain("[a]llow once"));
            Assert.That(shown, Does.Contain("[t]urn"));
            Assert.That(shown, Does.Contain("[s]ession"));
        });
    }

    [Test]
    public async Task Prompt_EscapesTerminalControlSequencesInPatch()
    {
        using var dir   = new TestTempDir();
        var       patch = "--- /dev/null\n+++ b/file.txt\n@@ -0,0 +1 @@\n+\u001b[2Jhidden\n";
        var preparation = PatchPreparation(dir, patch);
        using var output = new StringWriter();

        await PromptAsync(preparation, "d", output);

        TestAssert.DoesNotContain("\u001b", output.ToString(), StringComparison.Ordinal);
        Assert.That(output.ToString(), Does.Contain("+\\u001B[2Jhidden"));
    }

    [Test]
    public async Task Prompt_StatesRunScopedMeaningAndRestartBoundary()
    {
        using var dir   = new TestTempDir();
        var       patch = "--- a/file.txt\n+++ b/file.txt\n@@ -1 +1 @@\n-old\n+new\n";
        var       preparation = PatchPreparation(dir, patch);
        using var output = new StringWriter();

        await PromptAsync(preparation, "d", output, () =>
        {
            var shown = output.ToString();
            Assert.That(shown, Does.Contain("never survive a restart"));
        });
    }

    [Test]
    public async Task Prompt_NonInteractiveInput_IsUnavailable_NotDenial()
    {
        using var dir   = new TestTempDir();
        var       patch = "--- a/file.txt\n+++ b/file.txt\n@@ -1 +1 @@\n-old\n+new\n";
        var       preparation = PatchPreparation(dir, patch);
        using var output = new StringWriter();
        var request = new ApprovalRequest(preparation, PermissionLevel.Level2,
                                          [GrantScope.SingleOperation], null, null);

        var result = await new ConsoleApprovalProvider(new EofReader(), output)
            .PromptAsync(request, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Kind, Is.EqualTo(ApprovalResolutionKind.Unavailable));
            Assert.That(result.Reason, Does.Contain("not executed"));
        });
    }

    [Test]
    public async Task Prompt_ShowsReviewerNoteForEscalatedRequests()
    {
        using var dir   = new TestTempDir();
        var       patch = "--- a/file.txt\n+++ b/file.txt\n@@ -1 +1 @@\n-old\n+new\n";
        var       preparation = PatchPreparation(dir, patch);
        using var output = new StringWriter();
        var request = new ApprovalRequest(preparation, PermissionLevel.Level2,
                                          [GrantScope.SingleOperation], "AI 无法判断授权范围", null);

        await new ConsoleApprovalProvider(new StringReader("d"), output)
            .PromptAsync(request, CancellationToken.None);

        Assert.That(output.ToString(), Does.Contain("AI 无法判断授权范围"));
    }

    private static string Describe(ApprovalResolution resolution) => resolution.Kind switch
    {
        ApprovalResolutionKind.Granted when resolution.Scope == GrantScope.Turn  => "turn",
        ApprovalResolutionKind.Granted when resolution.Scope == GrantScope.Session => "session",
        ApprovalResolutionKind.Granted => "allow",
        _                              => "deny"
    };

    /// <summary>首次读取时执行断言的输入；断言看到的是读取前的完整输出。</summary>
    private sealed class InspectingReader(string answer, Action onRead) : TextReader
    {
        private bool _inspected;

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (!_inspected)
            {
                _inspected = true;
                onRead();
            }

            return ValueTask.FromResult<string?>(answer);
        }

        public override int Peek() => -1;

        public override int Read() => -1;
    }

    /// <summary>读取到 EOF 的输入。</summary>
    private sealed class EofReader : TextReader
    {
        public override int Peek() => -1;

        public override int Read() => -1;

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>(null);
    }
}
