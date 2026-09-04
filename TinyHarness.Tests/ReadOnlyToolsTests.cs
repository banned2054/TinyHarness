using System.Text;
using TinyHarness.Core.Agent;
using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Tools;

namespace TinyHarness.Tests;

public class ReadOnlyToolsTests
{
    private static ChatToolCall Call(ITool tool, string argumentsJson)
        => new("call_1", tool.Definition.Name, argumentsJson);

    private static ToolPreparation Prepare(ITool tool, string argumentsJson)
        => tool.Prepare(Call(tool, argumentsJson));

    private static async Task<ToolResult> ExecuteAsync(ITool tool, ToolPreparation preparation)
        => await tool.ExecuteAsync(preparation, CancellationToken.None);

    // ---- read_file ---------------------------------------------------------

    [Fact]
    public async Task ReadFile_ReturnsContent()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("src/a.txt", "hello\nworld\n");
        var tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"src/a.txt"}"""));

        Assert.True(result.Succeeded);
        Assert.Equal("hello\nworld", result.Content);
    }

    [Fact]
    public async Task ReadFile_PagesWithOffsetAndLimit()
    {
        using var dir   = new TestTempDir();
        var       lines = string.Concat(Enumerable.Range(1, 100).Select(i => $"line {i}\n"));
        dir.WriteFile("big.txt", lines);
        var tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"big.txt","offset":95,"limit":10}"""));

        Assert.True(result.Succeeded);
        Assert.Equal("line 95\nline 96\nline 97\nline 98\nline 99\nline 100", result.Content);
    }

    [Fact]
    public async Task ReadFile_MissingFile_ReturnsNotFound()
    {
        using var dir  = new TestTempDir();
        var       tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"missing.txt"}"""));

        Assert.False(result.Succeeded);
        Assert.Contains("File not found", result.Content);
    }

    [Fact]
    public async Task ReadFile_DirectoryPath_TellsModelToList()
    {
        using var dir = new TestTempDir();
        dir.CreateDirectory("src");
        var tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"src"}"""));

        Assert.False(result.Succeeded);
        Assert.Contains("use list_files", result.Content);
    }

    [Fact]
    public async Task ReadFile_TruncatesBeyondDefaultCap()
    {
        using var dir   = new TestTempDir();
        var       lines = string.Concat(Enumerable.Range(1, 5_000).Select(i => $"line {i}\n"));
        dir.WriteFile("huge.txt", lines);
        var tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"huge.txt"}"""));

        Assert.True(result.Succeeded);
        Assert.StartsWith("line 1", result.Content);
        Assert.Contains("truncated before line 4001; continue with offset=4001", result.Content);
        Assert.DoesNotContain("line 4001\n", result.Content); // line 4001 must not be rendered
    }

    [Fact]
    public async Task ReadFile_OneLinePastLimit_ReportsTruncation()
    {
        // Regression: the file has exactly limit+1 lines. The last line must not
        // be silently dropped; the truncation notice is required in this case.
        using var dir   = new TestTempDir();
        var       lines = string.Concat(Enumerable.Range(1, 11).Select(i => $"line {i}\n"));
        dir.WriteFile("eleven.txt", lines);
        var tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"eleven.txt","limit":10}"""));

        Assert.True(result.Succeeded);
        Assert.StartsWith("line 1", result.Content);
        Assert.DoesNotContain("line 11\n", result.Content); // content of line 11 must not be rendered
        Assert.Contains("truncated before line 11; continue with offset=11", result.Content);
    }

    [Fact]
    public async Task ReadFile_ExactFitAtLimit_NoTruncationNotice()
    {
        using var dir   = new TestTempDir();
        var       lines = string.Concat(Enumerable.Range(1, 10).Select(i => $"line {i}\n"));
        dir.WriteFile("ten.txt", lines);
        var tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"ten.txt","limit":10}"""));

        Assert.True(result.Succeeded);
        Assert.EndsWith("line 10", result.Content);
        Assert.DoesNotContain("truncated", result.Content);
    }

    [Fact]
    public void ReadFile_Prepare_RejectsLimitAboveHardMax()
    {
        using var dir  = new TestTempDir();
        var       tool = new ReadFileTool(dir.Workspace);

        var ex = Assert.Throws<InvalidDataException>(() => Prepare(tool, """{"path":"a.txt","limit":4001}"""));

        Assert.Contains("between 1 and 4000", ex.Message);
    }

    [Fact]
    public async Task ReadFile_TruncatesOversizedSingleLine_InPlace()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("minified.js", new string('a', 20_000) + "\ntail\n");
        var tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"minified.js"}"""));

        Assert.True(result.Succeeded);
        Assert.StartsWith(new string('a', 8192), result.Content);
        Assert.Contains("... [line 1 truncated]", result.Content);
        Assert.DoesNotContain(new string('a', 9_000), result.Content);
        Assert.EndsWith("tail", result.Content);
    }

    [Fact]
    public async Task ReadFile_TruncatedLongLine_NextPageStartsAfterIt()
    {
        // A long line cut in place has been "consumed": the next page must start
        // at the following line (offset 2), never by re-reading the cut line.
        using var dir = new TestTempDir();
        dir.WriteFile("one.txt", new string('a', 20_000) + "\nsecond\nthird\n");
        var tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"one.txt","limit":1}"""));

        Assert.True(result.Succeeded);
        Assert.Contains("... [line 1 truncated]", result.Content);
        Assert.Contains("truncated before line 2; continue with offset=2", result.Content);
        Assert.DoesNotContain("second", result.Content);
    }

    [Fact]
    public async Task ReadFile_StopsAtOutputCharBudget()
    {
        // 2000 lines x 80 chars far exceed the 64 KiB output budget before the
        // 4000-line cap ever matters. Each rendered line costs 81 chars (80 +
        // newline); the line area keeps the trailer reserve, so exactly 808 lines
        // fit and line 809 is the first one not shown.
        using var dir = new TestTempDir();
        var       sb  = new StringBuilder();
        for (var i = 1; i <= 2_000; i++)
        {
            sb.Append(i.ToString("D5")).Append(new string('x', 75)).Append('\n');
        }

        dir.WriteFile("wide.txt", sb.ToString());
        var tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"wide.txt"}"""));

        Assert.True(result.Succeeded);
        Assert.True(result.Content.Length <= 64 * 1024);
        Assert.Contains("00808", result.Content);
        Assert.DoesNotContain("00809", result.Content); // line 809 fully skipped
        Assert.Contains("truncated before line 809; continue with offset=809", result.Content);
    }

    [Fact]
    public async Task ReadFile_LineCut_DoesNotSplitSurrogatePair()
    {
        // The 8 KiB cut lands between the two code units of the emoji; the cut
        // must keep the pair together instead of emitting a dangling surrogate.
        using var dir = new TestTempDir();
        dir.WriteFile("emoji.txt", new string('a', 8191) + "\U0001F600" + new string('b', 10) + "\n");
        var tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"emoji.txt"}"""));

        Assert.True(result.Succeeded);
        Assert.StartsWith(new string('a', 8191) + "\U0001F600", result.Content);
        Assert.DoesNotContain(new string('a', 8192), result.Content);
    }

    [Fact]
    public async Task ReadFile_FileOverMaxBytes_IsRefusedNotRead()
    {
        using var dir = new TestTempDir();
        dir.WriteBytes("huge.txt", new byte[4 * 1024 * 1024 + 1]);
        var tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"huge.txt"}"""));

        Assert.True(result.Succeeded);
        Assert.Contains("4 MiB read limit", result.Content);
        Assert.Contains("search_text", result.Content);
    }

    [Fact]
    public async Task ReadFile_FileJustUnderMaxBytes_IsReadNotRefused()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("edge.txt", new string('x', 4 * 1024 * 1024 - 64) + "\n");
        var tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"edge.txt"}"""));

        Assert.True(result.Succeeded);
        Assert.DoesNotContain("read limit", result.Content);
        Assert.Contains("[line 1 truncated]", result.Content);
    }

    [Fact]
    public async Task ReadFile_BinaryFile_IsReportedNotDumped()
    {
        using var dir = new TestTempDir();
        dir.WriteBytes("blob.dat", [0x50, 0x4B, 0x00, 0x01, 0x02]);
        var tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"blob.dat"}"""));

        Assert.True(result.Succeeded);
        Assert.Contains("binary file", result.Content);
    }

    [Fact]
    public void ReadFile_Prepare_RejectsMissingPath()
    {
        using var dir  = new TestTempDir();
        var       tool = new ReadFileTool(dir.Workspace);

        var ex = Assert.Throws<InvalidDataException>(() => Prepare(tool, "{}"));
        Assert.Contains("'path'", ex.Message);
    }

    [Fact]
    public void ReadFile_Prepare_RejectsEscape()
    {
        using var dir  = new TestTempDir();
        var       tool = new ReadFileTool(dir.Workspace);

        Assert.Throws<InvalidDataException>(() => Prepare(tool, """{"path":"../secret.txt"}"""));
    }

    [Fact]
    public void ReadFile_Prepare_RejectsInvalidOffset()
    {
        using var dir  = new TestTempDir();
        var       tool = new ReadFileTool(dir.Workspace);

        Assert.Throws<InvalidDataException>(() => Prepare(tool, """{"path":"a.txt","offset":0}"""));
    }

    // ---- list_files --------------------------------------------------------

    [Fact]
    public async Task ListFiles_ListsTopLevelEntries()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("README.md", "x");
        dir.WriteFile("src/a.cs", "x");
        var tool = new ListFilesTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, "{}"));

        Assert.True(result.Succeeded);
        Assert.Contains("README.md", result.Content);
        Assert.Contains("src/", result.Content);
        Assert.DoesNotContain("src/a.cs", result.Content);
    }

    [Fact]
    public async Task ListFiles_Recursive_ShowsNestedFiles()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("src/deep/a.cs", "x");
        var tool = new ListFilesTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"recursive":true}"""));

        Assert.True(result.Succeeded);
        Assert.Contains("src/deep/a.cs", result.Content);
    }

    [Fact]
    public async Task ListFiles_OmitsBuildAndVcDirectories()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("obj/junk.cs", "x");
        dir.WriteFile(".git/config", "x");
        dir.WriteFile("src/real.cs", "x");
        var tool = new ListFilesTool(dir.Workspace);

        var recursive = await ExecuteAsync(tool, Prepare(tool, """{"recursive":true}"""));

        Assert.Contains("src/real.cs", recursive.Content);
        Assert.DoesNotContain("obj/", recursive.Content);
        Assert.DoesNotContain(".git/", recursive.Content);
        Assert.DoesNotContain("junk.cs", recursive.Content);

        // An explicitly requested excluded directory is still reachable.
        var explicitResult = await ExecuteAsync(tool, Prepare(tool, """{"path":"obj"}"""));
        Assert.Contains("junk.cs", explicitResult.Content);
    }

    [Fact]
    public async Task ListFiles_MissingDirectory_ReturnsNotFound()
    {
        using var dir  = new TestTempDir();
        var       tool = new ListFilesTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"nope"}"""));

        Assert.False(result.Succeeded);
        Assert.Contains("Directory not found", result.Content);
    }

    [Fact]
    public async Task ListFiles_MaxDepth_LimitsRecursion()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("src/a.cs", "x");
        dir.WriteFile("src/deep/b.cs", "x");
        dir.WriteFile("src/deep/deeper/c.cs", "x");
        var tool = new ListFilesTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"recursive":true,"maxDepth":1}"""));

        Assert.Contains("src/a.cs", result.Content);
        Assert.DoesNotContain("src/deep/b.cs", result.Content);
    }

    // ---- search_text -------------------------------------------------------

    [Fact]
    public async Task SearchText_FindsAcrossFiles_IgnoreCaseByDefault()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("a.txt", "The quick brown fox\njumps over\n");
        dir.WriteFile("b.txt", "nothing here\nquick AND dirty\n");
        var tool = new SearchTextTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"pattern":"QUICK"}"""));

        Assert.True(result.Succeeded);
        Assert.Contains("a.txt:1: The quick brown fox", result.Content);
        Assert.Contains("b.txt:2: quick AND dirty", result.Content);
    }

    [Fact]
    public async Task SearchText_CaseSensitive_Distinguishes()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("a.txt", "Quick\nquick\n");
        var tool = new SearchTextTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"pattern":"quick","caseSensitive":true}"""));

        Assert.True(result.Succeeded);
        Assert.Contains("a.txt:2: quick", result.Content);
        Assert.DoesNotContain("a.txt:1", result.Content);
    }

    [Fact]
    public async Task SearchText_StopsAtMaxResults()
    {
        using var dir   = new TestTempDir();
        var       lines = string.Concat(Enumerable.Range(1, 300).Select(i => $"needle {i}\n"));
        dir.WriteFile("haystack.txt", lines);
        var tool = new SearchTextTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"pattern":"needle","maxResults":50}"""));

        Assert.True(result.Succeeded);
        Assert.Contains("stopped after 50 matches", result.Content);
    }

    [Fact]
    public async Task SearchText_NoMatches_ReportsCleanly()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("a.txt", "hello\n");
        var tool = new SearchTextTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"pattern":"zzz"}"""));

        Assert.True(result.Succeeded);
        Assert.Contains("no matches", result.Content);
    }

    [Fact]
    public async Task SearchText_SkipsBinaryFiles()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("a.txt", "needle in text\n");
        dir.WriteBytes("b.bin", [0x00, 0x01, 0x02, .. Encoding.UTF8.GetBytes("needle")]);
        var tool = new SearchTextTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"pattern":"needle"}"""));

        Assert.True(result.Succeeded);
        Assert.Contains("a.txt:1: needle in text", result.Content);
        Assert.DoesNotContain("b.bin:1", result.Content);
    }

    [Fact]
    public async Task SearchText_WalkOverFileCap_ReportsPartialScan()
    {
        // More files than the walk cap: the result must say the scan was partial
        // instead of presenting "(no matches...)" as if the whole repo was seen.
        using var dir = new TestTempDir();
        for (var i = 0; i < 3_005; i++)
        {
            dir.WriteFile($"f{i:D4}.txt", "filler\n");
        }

        var tool = new SearchTextTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"pattern":"needle-partial-scan"}"""));

        Assert.True(result.Succeeded);
        Assert.Contains("no matches", result.Content);
        Assert.Contains("only the first 3000 files scanned", result.Content);
    }

    [Fact]
    public async Task SearchText_FileSymlinkPointingOutside_IsSkippedNotRead()
    {
        // A file symlink inside the workspace whose target is outside must not
        // have its content read or surfaced (PLAN §11).
        using var workspaceDir = new TestTempDir();
        using var outsideDir   = new TestTempDir();
        var       outsideFile  = outsideDir.WriteFile("secret.txt", "TOP-SECRET-PLACEHOLDER needle-outside\n");
        var       linkPath     = Path.Combine(workspaceDir.Root, "link.txt");
        if (!TryCreateFileSymlink(linkPath, outsideFile))
        {
            return; // No symlink privilege; nothing to verify.
        }

        var tool = new SearchTextTool(workspaceDir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"pattern":"needle-outside"}"""));

        Assert.True(result.Succeeded);
        Assert.DoesNotContain("TOP-SECRET-PLACEHOLDER", result.Content);
        Assert.Contains("file link(s) to outside the workspace", result.Content);
    }

    [Fact]
    public async Task SearchText_FileSymlinkInsideWorkspace_IsFollowed()
    {
        // A file symlink whose final target stays inside the workspace is still
        // searchable: the boundary rule is about the resolved target, not the
        // link itself (matches read_file semantics).
        using var dir      = new TestTempDir();
        var       target   = dir.WriteFile("real.txt", "needle-inside\n");
        var       linkPath = Path.Combine(dir.Root, "alias.txt");
        if (!TryCreateFileSymlink(linkPath, target))
        {
            return; // No symlink privilege; nothing to verify.
        }

        var tool = new SearchTextTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"pattern":"needle-inside"}"""));

        Assert.True(result.Succeeded);
        Assert.Contains("alias.txt:1: needle-inside", result.Content);
    }

    private static bool TryCreateFileSymlink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false; // No symlink privilege (Developer Mode/admin); skip.
        }
    }

    [Fact]
    public void SearchText_Prepare_RejectsEmptyPattern()
    {
        using var dir  = new TestTempDir();
        var       tool = new SearchTextTool(dir.Workspace);

        Assert.Throws<InvalidDataException>(() => Prepare(tool, """{"pattern":"  "}"""));
    }

    [Fact]
    public void SearchText_Prepare_RejectsEscape()
    {
        using var dir  = new TestTempDir();
        var       tool = new SearchTextTool(dir.Workspace);

        Assert.Throws<InvalidDataException>(() => Prepare(tool, """{"pattern":"x","path":"../outside"}"""));
    }

    // ---- Agent Loop over real read-only tools -----------------------------

    [Fact]
    public async Task AgentLoop_ReadToolResult_FeedsNextModelTurn()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("data.txt", "alpha\nbeta\n");
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"data.txt"}"""));
        client.Enqueue(FakeChatClient.Text("I read the file."));
        var loop = new AgentLoop(client, new ToolRegistry([new ReadFileTool(dir.Workspace)]), Options());

        var result = await loop.RunAsync("sys", "read data.txt", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Equal(2, client.Requests);
        Assert.Contains(loop.History, m => m.Role == ChatRole.Tool && m.Content == "alpha\nbeta");
    }

    [Fact]
    public async Task AgentLoop_ReadFailure_IsReportedBackAndLoopContinues()
    {
        using var dir    = new TestTempDir();
        var       client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"missing.txt"}"""));
        client.Enqueue(FakeChatClient.Text("cannot read"));
        var loop = new AgentLoop(client, new ToolRegistry([new ReadFileTool(dir.Workspace)]), Options());

        var result = await loop.RunAsync("sys", "read missing.txt", CancellationToken.None);

        Assert.Equal(AgentStatus.Completed, result.Status);
        Assert.Contains(loop.History,
                        m => m.Role == ChatRole.Tool && m.Content.Contains("File not found"));
    }

    private static AgentOptions Options() => new()
    {
        Model                     = "test-model",
        MaxAgentSteps             = 10,
        DefaultToolTimeoutSeconds = 30,
    };
}
