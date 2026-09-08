using System.Text.Json.Nodes;
using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Tools;

namespace TinyHarness.Tests;

public class ApplyPatchToolTests
{
    [Theory]
    [InlineData(0, "x\na\nb\n")]
    [InlineData(1, "a\nx\nb\n")]
    [InlineData(2, "a\nb\nx\n")]
    public async Task ApplyPatch_PureInsertionUsesTheLineAfterTheOldRange(int oldStart, string expected)
    {
        using var dir = new TestTempDir();
        var path = dir.WriteFile("file.txt", "a\nb\n");
        var tool = new ApplyPatchTool(dir.Workspace);
        var patch = $"--- a/file.txt\n+++ b/file.txt\n@@ -{oldStart},0 +{oldStart + 1},1 @@\n+x\n";

        var result = await ExecuteAsync(tool, Prepare(tool, patch));

        Assert.True(result.Succeeded, result.Content);
        Assert.Equal(expected, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ApplyPatch_InsertionPastEndFailsWithoutWriting()
    {
        using var dir = new TestTempDir();
        var path = dir.WriteFile("file.txt", "a\n");
        var tool = new ApplyPatchTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool,
            "--- a/file.txt\n+++ b/file.txt\n@@ -3,0 +4,1 @@\n+x\n"));

        Assert.False(result.Succeeded);
        Assert.Contains("does not match", result.Content);
        Assert.Equal("a\n", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task ApplyPatch_RemovingAllLinesProducesAnEmptyFile(string newline)
    {
        using var dir = new TestTempDir();
        var path = dir.WriteFile("file.txt", "a" + newline);
        var tool = new ApplyPatchTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool,
            "--- a/file.txt\n+++ b/file.txt\n@@ -1,1 +0,0 @@\n-a\n"));

        Assert.True(result.Succeeded, result.Content);
        Assert.Empty(await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ApplyPatch_HeaderLikeBodyLinesDoNotEndAHunk()
    {
        using var dir = new TestTempDir();
        var first = dir.WriteFile("first.sql", "-- comment\nkeep\n");
        var second = dir.WriteFile("second.txt", "old\n");
        var tool = new ApplyPatchTool(dir.Workspace);

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

        Assert.True(result.Succeeded, result.Content);
        Assert.Equal("++ replacement\nkept\n", await File.ReadAllTextAsync(first));
        Assert.Equal("new\n", await File.ReadAllTextAsync(second));
    }

    [Theory]
    [InlineData("")]
    [InlineData("existing\n")]
    public async Task ApplyPatch_NewFileCollisionPreventsEveryWrite(string existing)
    {
        using var dir = new TestTempDir();
        var first = dir.WriteFile("first.txt", "old\n");
        var collision = dir.WriteFile("exists.txt", existing);
        var tool = new ApplyPatchTool(dir.Workspace);

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

        Assert.False(result.Succeeded);
        Assert.Contains("already exists", result.Content);
        Assert.Equal("old\n", await File.ReadAllTextAsync(first));
        Assert.Equal(existing, await File.ReadAllTextAsync(collision));
    }

    [Fact]
    public void ApplyPatch_RejectsOversizedPatchBeforeParsing()
    {
        using var dir = new TestTempDir();
        var tool = new ApplyPatchTool(dir.Workspace);
        var patch = "--- /dev/null\n+++ b/new.txt\n@@ -0,0 +1 @@\n+" + new string('x', 256 * 1024);

        var error = Assert.Throws<InvalidDataException>(() => Prepare(tool, patch));

        Assert.Contains("character limit", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Root));
    }

    [Fact]
    public void ApplyPatch_RejectsTooManyFiles()
    {
        using var dir = new TestTempDir();
        var tool = new ApplyPatchTool(dir.Workspace);
        var patch = string.Concat(Enumerable.Range(0, 65).Select(index =>
            $"--- /dev/null\n+++ b/{index}.txt\n@@ -0,0 +1 @@\n+x\n"));

        var error = Assert.Throws<InvalidDataException>(() => Prepare(tool, patch));

        Assert.Contains("file limit", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(dir.Root));
    }

    [Fact]
    public async Task ApplyPatch_RejectsOversizedInputBeforeAnyWrite()
    {
        using var dir = new TestTempDir();
        var small = dir.WriteFile("small.txt", "old\n");
        var large = dir.WriteFile("large.txt", new string('x', 4 * 1024 * 1024 + 1));
        var tool = new ApplyPatchTool(dir.Workspace);

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

        Assert.False(result.Succeeded);
        Assert.Contains("read limit", result.Content);
        Assert.Equal("old\n", await File.ReadAllTextAsync(small));
        Assert.Equal(new string('x', 4 * 1024 * 1024 + 1), await File.ReadAllTextAsync(large));
    }

    [Fact]
    public async Task ApplyPatch_RejectsOversizedOutput()
    {
        using var dir = new TestTempDir();
        var original = new string('x', 4 * 1024 * 1024 - 1) + "\n";
        var path = dir.WriteFile("large.txt", original);
        var tool = new ApplyPatchTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool,
            "--- a/large.txt\n+++ b/large.txt\n@@ -0,0 +1 @@\n+x\n"));

        Assert.False(result.Succeeded);
        Assert.Contains("Output", result.Content);
        Assert.Contains("file limit", result.Content);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ApplyPatch_CombinedBudgetFailurePreventsEveryWrite()
    {
        using var dir = new TestTempDir();
        var original = "old\n" + new string('x', 3 * 1024 * 1024);
        var paths = Enumerable.Range(0, 3).Select(index => dir.WriteFile($"{index}.txt", original)).ToArray();
        var patch = string.Concat(Enumerable.Range(0, 3).Select(index =>
            $"--- a/{index}.txt\n+++ b/{index}.txt\n@@ -1 +1 @@\n-old\n+new\n"));
        var tool = new ApplyPatchTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, patch));

        Assert.False(result.Succeeded);
        Assert.Contains("budget", result.Content);
        foreach (var path in paths)
        {
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
    }

    [Fact]
    public async Task ApplyPatch_AllowsInputAndOutputAtTheFileLimit()
    {
        using var dir = new TestTempDir();
        var tail = new string('x', 4 * 1024 * 1024 - 4);
        var path = dir.WriteFile("large.txt", "old\n" + tail);
        var tool = new ApplyPatchTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool,
            "--- a/large.txt\n+++ b/large.txt\n@@ -1 +1 @@\n-old\n+new\n"));

        Assert.True(result.Succeeded, result.Content);
        Assert.Equal("new\n" + tail, await File.ReadAllTextAsync(path));
    }

    private static ChatToolCall Call(string argumentsJson) => new("call_1", "apply_patch", argumentsJson);

    private static ToolPreparation Prepare(ApplyPatchTool tool, string patch)
        => tool.Prepare(Call(PatchArgs(patch)));

    private static string PatchArgs(string patch)
        => new JsonObject { ["patch"] = patch }.ToJsonString();

    private static async Task<ToolResult> ExecuteAsync(ApplyPatchTool tool, ToolPreparation preparation)
        => await tool.ExecuteAsync(preparation, CancellationToken.None);

    [Fact]
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

        Assert.Equal("filesystem.write", preparation.Capability);
        Assert.Equal([Path.Combine(dir.Root, "src", "fixme.cs")], preparation.TargetPaths);

        var result = await ExecuteAsync(tool, preparation);

        Assert.True(result.Succeeded);
        Assert.Contains("Applied patch to 1 file(s)", result.Content);
        Assert.Contains("src/fixme.cs", result.Content);
        Assert.Equal("line1\nline2-fixed\nline3\n", File.ReadAllText(Path.Combine(dir.Root, "src", "fixme.cs")));
    }

    [Fact]
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

        Assert.True(result.Succeeded);
        Assert.Contains("2 file(s)", result.Content);
        Assert.Equal("a1-x\na2\n", File.ReadAllText(Path.Combine(dir.Root, "f1.txt")));
        Assert.Equal("b1\nb2-y\n", File.ReadAllText(Path.Combine(dir.Root, "f2.txt")));
    }

    [Fact]
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

        Assert.True(result.Succeeded, result.Content);
        Assert.Equal("alpha\nbeta\n", File.ReadAllText(Path.Combine(dir.Root, "new.cs")));
    }

    [Fact]
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

        Assert.False(result.Succeeded);
        Assert.Contains("context mismatch", result.Content);
        Assert.Equal("line1\nline2\nline3\n", File.ReadAllText(path)); // untouched
    }

    [Fact]
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

        Assert.False(result.Succeeded);
        Assert.Contains("File not found", result.Content);
    }

    [Fact]
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

        Assert.True(result.Succeeded);
        Assert.Equal("line1\r\nline2-fixed\r\nline3\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void ApplyPatch_Prepare_RejectsPathEscape()
    {
        using var dir  = new TestTempDir();
        var       tool = new ApplyPatchTool(dir.Workspace);

        var ex = Assert.Throws<InvalidDataException>(() => Prepare(tool, """
                                                                   --- a/x.cs
                                                                   +++ b/../outside.cs
                                                                   @@ -1 +1 @@
                                                                   -a
                                                                   +b
                                                                   """));

        Assert.Contains("escapes the workspace root", ex.Message);
    }

    [Fact]
    public void ApplyPatch_Prepare_RejectsFileDeletion()
    {
        using var dir  = new TestTempDir();
        var       tool = new ApplyPatchTool(dir.Workspace);

        var ex = Assert.Throws<InvalidDataException>(() => Prepare(tool, """
                                                                   --- a/fixme.cs
                                                                   +++ /dev/null
                                                                   @@ -1 +0,0 @@
                                                                   -line1
                                                                   """));

        Assert.Contains("not supported", ex.Message);
    }

    [Fact]
    public void ApplyPatch_Prepare_RejectsMissingHeader()
    {
        using var dir  = new TestTempDir();
        var       tool = new ApplyPatchTool(dir.Workspace);

        Assert.Throws<InvalidDataException>(() => Prepare(tool, "no unified diff here\n"));
    }

    [Fact]
    public void ApplyPatch_Prepare_RejectsEmptyPatch()
    {
        using var dir  = new TestTempDir();
        var       tool = new ApplyPatchTool(dir.Workspace);

        Assert.Throws<InvalidDataException>(() => Prepare(tool, "   "));
    }

    [Fact]
    public void ApplyPatch_Prepare_RejectsNoNewlineMarker()
    {
        using var dir  = new TestTempDir();
        var       tool = new ApplyPatchTool(dir.Workspace);

        var ex = Assert.Throws<InvalidDataException>(() => Prepare(tool, """
                                                                   --- a/fixme.cs
                                                                   +++ b/fixme.cs
                                                                   @@ -1 +1 @@
                                                                   -line1
                                                                   +line1-x
                                                                   \ No newline at end of file
                                                                   """));

        Assert.Contains("No newline at end of file", ex.Message);
    }

    [Fact]
    public void ApplyPatch_Prepare_RejectsDuplicateTarget()
    {
        using var dir  = new TestTempDir();
        var       tool = new ApplyPatchTool(dir.Workspace);

        // Two sections for the same file: applying them sequentially from the
        // same original content would silently overwrite the first edit, so
        // Prepare must reject the patch instead.
        var ex = Assert.Throws<InvalidDataException>(() => Prepare(tool, """
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

        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
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

        Assert.True(result.Succeeded, result.Content);
        Assert.Equal("line1-fixed\n", File.ReadAllText(inside));
        Assert.Equal("outside\n", File.ReadAllText(outside));
    }

    [Fact]
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

        Assert.False(result.Succeeded);
        Assert.Contains("overlap", result.Content);
        Assert.Equal("line1\nline2\nline3\n", File.ReadAllText(path)); // untouched
    }
}
