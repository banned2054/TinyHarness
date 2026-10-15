using System.Text.Json.Nodes;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

public class ApplyPatchToolTests
{
    [TestCase(0, "x\na\nb\n")]
    [TestCase(1, "a\nx\nb\n")]
    [TestCase(2, "a\nb\nx\n")]
    public async Task ApplyPatch_PureInsertionUsesTheLineAfterTheOldRange(int oldStart, string expected)
    {
        using var dir   = new TestTempDir();
        var       path  = dir.WriteFile("file.txt", "a\nb\n");
        var       tool  = new ApplyPatchTool(dir.Workspace);
        var       patch = $"--- a/file.txt\n+++ b/file.txt\n@@ -{oldStart},0 +{oldStart + 1},1 @@\n+x\n";

        var result = await ExecuteAsync(tool, Prepare(tool, patch));

        Assert.That(result.Succeeded, Is.True, result.Content);
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(expected));
    }

    [Test]
    public async Task ApplyPatch_InsertionPastEndFailsWithoutWriting()
    {
        using var dir  = new TestTempDir();
        var       path = dir.WriteFile("file.txt", "a\n");
        var       tool = new ApplyPatchTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, "--- a/file.txt\n+++ b/file.txt\n@@ -3,0 +4,1 @@\n+x\n"));

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Content, Does.Contain("does not match"));
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo("a\n"));
    }


    [TestCase("\n")]
    [TestCase("\r\n")]
    public async Task ApplyPatch_RemovingAllLinesProducesAnEmptyFile(string newline)
    {
        using var dir  = new TestTempDir();
        var       path = dir.WriteFile("file.txt", "a" + newline);
        var       tool = new ApplyPatchTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, "--- a/file.txt\n+++ b/file.txt\n@@ -1,1 +0,0 @@\n-a\n"));

        Assert.That(result.Succeeded, Is.True, result.Content);
        Assert.That(await File.ReadAllBytesAsync(path), Is.Empty);
    }

    [Test]
    public async Task ApplyPatch_HeaderLikeBodyLinesDoNotEndAHunk()
    {
        using var dir    = new TestTempDir();
        var       first  = dir.WriteFile("first.sql", "-- comment\nkeep\n");
        var       second = dir.WriteFile("second.txt", "old\n");
        var       tool   = new ApplyPatchTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """
                                                      --- a/first.sql
                                                      +++ b/first.sql
                                                      @@ -1 +1 @@
                                                      --- comment
                                                      +++ replacement
                                                      @@ -2 +2 @@
                                                      -keep
                                                      +kept
                                                      --- a/second.txt
                                                      +++ b/second.txt
                                                      @@ -1 +1 @@
                                                      -old
                                                      +new
                                                      """));

        Assert.That(result.Succeeded, Is.True, result.Content);
        Assert.That(await File.ReadAllTextAsync(first), Is.EqualTo("++ replacement\nkept\n"));
        Assert.That(await File.ReadAllTextAsync(second), Is.EqualTo("new\n"));
    }


    [TestCase("")]
    [TestCase("existing\n")]
    public async Task ApplyPatch_NewFileCollisionPreventsEveryWrite(string existing)
    {
        using var dir       = new TestTempDir();
        var       first     = dir.WriteFile("first.txt", "old\n");
        var       collision = dir.WriteFile("exists.txt", existing);
        var       tool      = new ApplyPatchTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """
                                                      --- a/first.txt
                                                      +++ b/first.txt
                                                      @@ -1 +1 @@
                                                      -old
                                                      +new
                                                      --- /dev/null
                                                      +++ b/exists.txt
                                                      @@ -0,0 +1 @@
                                                      +created
                                                      """));

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Content, Does.Contain("already exists"));
        Assert.That(await File.ReadAllTextAsync(first), Is.EqualTo("old\n"));
        Assert.That(await File.ReadAllTextAsync(collision), Is.EqualTo(existing));
    }

    [Test]
    public void ApplyPatch_RejectsOversizedPatchBeforeParsing()
    {
        using var dir   = new TestTempDir();
        var       tool  = new ApplyPatchTool(dir.Workspace);
        var       patch = "--- /dev/null\n+++ b/new.txt\n@@ -0,0 +1 @@\n+" + new string('x', 256 * 1024);

        var error = TestAssert.Throws<InvalidDataException>(() => Prepare(tool, patch));

        Assert.That(error.Message, Does.Contain("character limit"));
        Assert.That(Directory.EnumerateFileSystemEntries(dir.Root), Is.Empty);
    }

    [Test]
    public void ApplyPatch_RejectsTooManyFiles()
    {
        using var dir  = new TestTempDir();
        var       tool = new ApplyPatchTool(dir.Workspace);
        var patch = string.Concat(Enumerable.Range(0, 65).Select(index =>
                                                                     $"--- /dev/null\n+++ b/{index}.txt\n@@ -0,0 +1 @@\n+x\n"));

        var error = TestAssert.Throws<InvalidDataException>(() => Prepare(tool, patch));

        Assert.That(error.Message, Does.Contain("file limit"));
        Assert.That(Directory.EnumerateFileSystemEntries(dir.Root), Is.Empty);
    }

    [Test]
    public async Task ApplyPatch_RejectsOversizedInputBeforeAnyWrite()
    {
        using var dir   = new TestTempDir();
        var       small = dir.WriteFile("small.txt", "old\n");
        var       large = dir.WriteFile("large.txt", new string('x', 4 * 1024 * 1024 + 1));
        var       tool  = new ApplyPatchTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """
                                                      --- a/small.txt
                                                      +++ b/small.txt
                                                      @@ -1 +1 @@
                                                      -old
                                                      +new
                                                      --- a/large.txt
                                                      +++ b/large.txt
                                                      @@ -0,0 +1 @@
                                                      +inserted
                                                      """));

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Content, Does.Contain("read limit"));
        Assert.That(await File.ReadAllTextAsync(small), Is.EqualTo("old\n"));
        Assert.That(await File.ReadAllTextAsync(large), Is.EqualTo(new string('x', 4 * 1024 * 1024 + 1)));
    }

    [Test]
    public async Task ApplyPatch_RejectsOversizedOutput()
    {
        using var dir      = new TestTempDir();
        var       original = new string('x', 4 * 1024 * 1024 - 1) + "\n";
        var       path     = dir.WriteFile("large.txt", original);
        var       tool     = new ApplyPatchTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool,
                                                      "--- a/large.txt\n+++ b/large.txt\n@@ -0,0 +1 @@\n+x\n"));

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Content, Does.Contain("Output"));
        Assert.That(result.Content, Does.Contain("file limit"));
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(original));
    }

    [Test]
    public async Task ApplyPatch_CombinedBudgetFailurePreventsEveryWrite()
    {
        using var dir      = new TestTempDir();
        var       original = "old\n" + new string('x', 3 * 1024 * 1024);
        var       paths    = Enumerable.Range(0, 3).Select(index => dir.WriteFile($"{index}.txt", original)).ToArray();
        var patch = string.Concat(Enumerable.Range(0, 3).Select(index =>
                                                                    $"--- a/{index}.txt\n+++ b/{index}.txt\n@@ -1 +1 @@\n-old\n+new\n"));
        var tool = new ApplyPatchTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, patch));

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Content, Does.Contain("budget"));
        foreach (var path in paths) Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo(original));
    }

    [Test]
    public async Task ApplyPatch_AllowsInputAndOutputAtTheFileLimit()
    {
        using var dir  = new TestTempDir();
        var       tail = new string('x', 4 * 1024 * 1024    - 4);
        var       path = dir.WriteFile("large.txt", "old\n" + tail);
        var       tool = new ApplyPatchTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool,
                                                      "--- a/large.txt\n+++ b/large.txt\n@@ -1 +1 @@\n-old\n+new\n"));

        Assert.That(result.Succeeded, Is.True, result.Content);
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo("new\n" + tail));
    }

    private static ChatToolCall Call(string argumentsJson) => new("call_1", "apply_patch", argumentsJson);

    private static ToolPreparation Prepare(ApplyPatchTool tool, string patch) => tool.Prepare(Call(PatchArgs(patch)));

    private static string PatchArgs(string patch) => new JsonObject { ["patch"] = patch }.ToJsonString();

    private static async Task<ToolResult> ExecuteAsync(ApplyPatchTool tool, ToolPreparation preparation) =>
        await tool.ExecuteAsync(preparation, CancellationToken.None);

    [Test]
    public async Task ApplyPatch_ModifiesASingleLine()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("src/fixme.cs", "line1\nline2\nline3\n");
        var tool = new ApplyPatchTool(dir.Workspace);

        var preparation = Prepare(tool, """
                                  --- a/src/fixme.cs
                                  +++ b/src/fixme.cs
                                  @@ -2 +2 @@
                                  -line2
                                  +line2-fixed
                                  """);

        Assert.That(preparation.Capability, Is.EqualTo("filesystem.write"));
        Assert.That(preparation.TargetPaths, Is.EqualTo([Path.Combine(dir.Root, "src", "fixme.cs")]));

        var result = await ExecuteAsync(tool, preparation);

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.Contain("Applied patch to 1 file(s)"));
        Assert.That(result.Content, Does.Contain("src/fixme.cs"));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(dir.Root, "src", "fixme.cs")),
                    Is.EqualTo("line1\nline2-fixed\nline3\n"));
    }

    [Test]
    public async Task ApplyPatch_AppliesMultipleFiles()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("f1.txt", "a1\na2\n");
        dir.WriteFile("f2.txt", "b1\nb2\n");
        var tool = new ApplyPatchTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """
                                                      --- a/f1.txt
                                                      +++ b/f1.txt
                                                      @@ -1 +1 @@
                                                      -a1
                                                      +a1-x
                                                      --- a/f2.txt
                                                      +++ b/f2.txt
                                                      @@ -2 +2 @@
                                                      -b2
                                                      +b2-y
                                                      """));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.Contain("2 file(s)"));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(dir.Root, "f1.txt")), Is.EqualTo("a1-x\na2\n"));
        Assert.That(await File.ReadAllTextAsync(Path.Combine(dir.Root, "f2.txt")), Is.EqualTo("b1\nb2-y\n"));
    }

    [Test]
    public async Task ApplyPatch_CreatesANewFile()
    {
        using var dir  = new TestTempDir();
        var       tool = new ApplyPatchTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """
                                                      --- /dev/null
                                                      +++ b/new.cs
                                                      @@ -0,0 +1,2 @@
                                                      +alpha
                                                      +beta
                                                      """));

        Assert.That(result.Succeeded, Is.True, result.Content);
        Assert.That(await File.ReadAllTextAsync(Path.Combine(dir.Root, "new.cs")), Is.EqualTo("alpha\nbeta\n"));
    }

    [Test]
    public async Task ApplyPatch_ContextMismatch_WritesNothing()
    {
        using var dir  = new TestTempDir();
        var       path = dir.WriteFile("fixme.cs", "line1\nline2\nline3\n");
        var       tool = new ApplyPatchTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """
                                                      --- a/fixme.cs
                                                      +++ b/fixme.cs
                                                      @@ -2 +2 @@
                                                      -lineX
                                                      +line2-fixed
                                                      """));

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Content, Does.Contain("context mismatch"));
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo("line1\nline2\nline3\n")); // 文件未被改动
    }

    [Test]
    public async Task ApplyPatch_MissingFile_ReturnsNotFound()
    {
        using var dir  = new TestTempDir();
        var       tool = new ApplyPatchTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """
                                                      --- a/missing.cs
                                                      +++ b/missing.cs
                                                      @@ -1 +1 @@
                                                      -a
                                                      +b
                                                      """));

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Content, Does.Contain("File not found"));
    }

    [Test]
    public async Task ApplyPatch_PreservesCrlfLineEndings()
    {
        using var dir  = new TestTempDir();
        var       path = dir.WriteFile("win.cs", "line1\r\nline2\r\nline3\r\n");
        var       tool = new ApplyPatchTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """
                                                      --- a/win.cs
                                                      +++ b/win.cs
                                                      @@ -2 +2 @@
                                                      -line2
                                                      +line2-fixed
                                                      """));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo("line1\r\nline2-fixed\r\nline3\r\n"));
    }

    [Test]
    public void ApplyPatch_Prepare_RejectsPathEscape()
    {
        using var dir  = new TestTempDir();
        var       tool = new ApplyPatchTool(dir.Workspace);

        var ex = TestAssert.Throws<InvalidDataException>(() => Prepare(tool, """
                                                                       --- a/x.cs
                                                                       +++ b/../outside.cs
                                                                       @@ -1 +1 @@
                                                                       -a
                                                                       +b
                                                                       """));

        Assert.That(ex.Message, Does.Contain("escapes the workspace root"));
    }

    [Test]
    public void ApplyPatch_Prepare_RejectsFileDeletion()
    {
        using var dir  = new TestTempDir();
        var       tool = new ApplyPatchTool(dir.Workspace);

        var ex = TestAssert.Throws<InvalidDataException>(() => Prepare(tool, """
                                                                       --- a/fixme.cs
                                                                       +++ /dev/null
                                                                       @@ -1 +0,0 @@
                                                                       -line1
                                                                       """));

        Assert.That(ex.Message, Does.Contain("not supported"));
    }

    [Test]
    public void ApplyPatch_Prepare_RejectsMissingHeader()
    {
        using var dir  = new TestTempDir();
        var       tool = new ApplyPatchTool(dir.Workspace);

        TestAssert.Throws<InvalidDataException>(() => Prepare(tool, "no unified diff here\n"));
    }

    [Test]
    public void ApplyPatch_Prepare_RejectsEmptyPatch()
    {
        using var dir  = new TestTempDir();
        var       tool = new ApplyPatchTool(dir.Workspace);

        TestAssert.Throws<InvalidDataException>(() => Prepare(tool, "   "));
    }

    [Test]
    public void ApplyPatch_Prepare_RejectsNoNewlineMarker()
    {
        using var dir  = new TestTempDir();
        var       tool = new ApplyPatchTool(dir.Workspace);

        var ex = TestAssert.Throws<InvalidDataException>(() => Prepare(tool, """
                                                                       --- a/fixme.cs
                                                                       +++ b/fixme.cs
                                                                       @@ -1 +1 @@
                                                                       -line1
                                                                       +line1-x
                                                                       \ No newline at end of file
                                                                       """));

        Assert.That(ex.Message, Does.Contain("No newline at end of file"));
    }

    [Test]
    public void ApplyPatch_Prepare_RejectsDuplicateTarget()
    {
        using var dir  = new TestTempDir();
        var       tool = new ApplyPatchTool(dir.Workspace);

        // 同一文件的两个 section：从同一原始内容顺序应用会静默覆盖第一处修改，
        // 因此 Prepare 必须拒绝该补丁。
        var ex = TestAssert.Throws<InvalidDataException>(() => Prepare(tool, """
                                                                       --- a/fixme.cs
                                                                       +++ b/fixme.cs
                                                                       @@ -1 +1 @@
                                                                       -line1
                                                                       +line1-x
                                                                       --- a/fixme.cs
                                                                       +++ b/fixme.cs
                                                                       @@ -2 +2 @@
                                                                       -line2
                                                                       +line2-y
                                                                       """));

        Assert.That(ex.Message, Does.Contain("more than once"));
    }

    [Test]
    public async Task ApplyPatch_PreparedPlanIsIsolatedFromExposedArguments()
    {
        using var workspaceDir = new TestTempDir();
        using var outsideDir   = new TestTempDir();
        var       inside       = workspaceDir.WriteFile("fixme.cs", "line1\n");
        var       outside      = outsideDir.WriteFile("outside.cs", "outside\n");
        var       tool         = new ApplyPatchTool(workspaceDir.Workspace);

        var preparation = Prepare(tool, """
                                  --- a/fixme.cs
                                  +++ b/fixme.cs
                                  @@ -1 +1 @@
                                  -line1
                                  +line1-fixed
                                  """);

        var exposedPlan = (JsonArray)preparation.Arguments["_plan"]!;
        ((JsonObject)exposedPlan[0]!)["path"] = outside;

        var result = await ExecuteAsync(tool, preparation);

        Assert.That(result.Succeeded, Is.True, result.Content);
        Assert.That(await File.ReadAllTextAsync(inside), Is.EqualTo("line1-fixed\n"));
        Assert.That(await File.ReadAllTextAsync(outside), Is.EqualTo("outside\n"));
    }

    [Test]
    public async Task ApplyPatch_OverlappingHunks_WritesNothing()
    {
        using var dir  = new TestTempDir();
        var       path = dir.WriteFile("fixme.cs", "line1\nline2\nline3\n");
        var       tool = new ApplyPatchTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """
                                                      --- a/fixme.cs
                                                      +++ b/fixme.cs
                                                      @@ -1,2 +1,2 @@
                                                      -line1
                                                      -line2
                                                      +x
                                                      +y
                                                      @@ -2,1 +2,1 @@
                                                      -line2
                                                      +z
                                                      """));

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Content, Does.Contain("overlap"));
        Assert.That(await File.ReadAllTextAsync(path), Is.EqualTo("line1\nline2\nline3\n")); // 文件未被改动
    }
}
