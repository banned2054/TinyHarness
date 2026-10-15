using System.Text;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Agent;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

public class ReadOnlyToolsTests
{
    private static ChatToolCall Call(ITool tool, string argumentsJson) =>
        new("call_1", tool.Definition.Name, argumentsJson);

    private static ToolPreparation Prepare(ITool tool, string argumentsJson) => tool.Prepare(Call(tool, argumentsJson));

    private static async Task<ToolResult> ExecuteAsync(ITool tool, ToolPreparation preparation) =>
        await tool.ExecuteAsync(preparation, CancellationToken.None);

    // ---- read_file ---------------------------------------------------------

    [Test]
    public async Task ReadFile_ReturnsContent()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("src/a.txt", "hello\nworld\n");
        var tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"src/a.txt"}"""));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Is.EqualTo("hello\nworld"));
    }

    [Test]
    public async Task ReadFile_PagesWithOffsetAndLimit()
    {
        using var dir   = new TestTempDir();
        var       lines = string.Concat(Enumerable.Range(1, 100).Select(i => $"line {i}\n"));
        dir.WriteFile("big.txt", lines);
        var tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"big.txt","offset":95,"limit":10}"""));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Is.EqualTo("line 95\nline 96\nline 97\nline 98\nline 99\nline 100"));
    }

    [Test]
    public async Task ReadFile_MissingFile_ReturnsNotFound()
    {
        using var dir  = new TestTempDir();
        var       tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"missing.txt"}"""));

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Content, Does.Contain("File not found"));
    }

    [Test]
    public async Task ReadFile_DirectoryPath_TellsModelToList()
    {
        using var dir = new TestTempDir();
        dir.CreateDirectory("src");
        var tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"src"}"""));

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Content, Does.Contain("use list_files"));
    }

    [Test]
    public async Task ReadFile_TruncatesBeyondDefaultCap()
    {
        using var dir   = new TestTempDir();
        var       lines = string.Concat(Enumerable.Range(1, 5_000).Select(i => $"line {i}\n"));
        dir.WriteFile("huge.txt", lines);
        var tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"huge.txt"}"""));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.StartWith("line 1"));
        Assert.That(result.Content, Does.Contain("truncated before line 4001; continue with offset=4001"));
        Assert.That(result.Content, Does.Not.Contain("line 4001\n")); // 第 4001 行不得渲染
    }

    [Test]
    public async Task ReadFile_OneLinePastLimit_ReportsTruncation()
    {
        // 文件恰好 limit+1 行：最后一行不能被静默丢弃，此时必须给出截断提示。
        using var dir   = new TestTempDir();
        var       lines = string.Concat(Enumerable.Range(1, 11).Select(i => $"line {i}\n"));
        dir.WriteFile("eleven.txt", lines);
        var tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"eleven.txt","limit":10}"""));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.StartWith("line 1"));
        Assert.That(result.Content, Does.Not.Contain("line 11\n")); // 第 11 行内容不得渲染
        Assert.That(result.Content, Does.Contain("truncated before line 11; continue with offset=11"));
    }

    [Test]
    public async Task ReadFile_ExactFitAtLimit_NoTruncationNotice()
    {
        using var dir   = new TestTempDir();
        var       lines = string.Concat(Enumerable.Range(1, 10).Select(i => $"line {i}\n"));
        dir.WriteFile("ten.txt", lines);
        var tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"ten.txt","limit":10}"""));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.EndWith("line 10"));
        Assert.That(result.Content, Does.Not.Contain("truncated"));
    }

    [Test]
    public void ReadFile_Prepare_RejectsLimitAboveHardMax()
    {
        using var dir  = new TestTempDir();
        var       tool = new ReadFileTool(dir.Workspace);

        var ex = TestAssert.Throws<InvalidDataException>(() => Prepare(tool, """{"path":"a.txt","limit":4001}"""));

        Assert.That(ex.Message, Does.Contain("between 1 and 4000"));
    }

    [Test]
    public async Task ReadFile_TruncatesOversizedSingleLine_InPlace()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("minified.js", new string('a', 20_000) + "\ntail\n");
        var tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"minified.js"}"""));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.StartWith(new string('a', 8192)));
        Assert.That(result.Content, Does.Contain("... [line 1 truncated]"));
        Assert.That(result.Content, Does.Not.Contain(new string('a', 9_000)));
        Assert.That(result.Content, Does.EndWith("tail"));
    }

    [Test]
    public async Task ReadFile_TruncatedLongLine_NextPageStartsAfterIt()
    {
        // 原地截断的长行视为已"消费"：下一页必须从其后一行（offset 2）开始，
        // 不得重读被截断行。
        using var dir = new TestTempDir();
        dir.WriteFile("one.txt", new string('a', 20_000) + "\nsecond\nthird\n");
        var tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"one.txt","limit":1}"""));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.Contain("... [line 1 truncated]"));
        Assert.That(result.Content, Does.Contain("truncated before line 2; continue with offset=2"));
        Assert.That(result.Content, Does.Not.Contain("second"));
    }

    [Test]
    public async Task ReadFile_StopsAtOutputCharBudget()
    {
        // 2000 行 × 80 字符远超 64 KiB 输出预算，且先于 4000 行上限生效。每行渲染成本 81 字符
        // （80 + 换行）；行区域保留尾部提示空间后恰好容纳 808 行，第 809 行是第一行未显示的行。
        using var dir = new TestTempDir();
        var       sb  = new StringBuilder();
        for (var i = 1; i <= 2_000; i++) sb.Append(i.ToString("D5")).Append(new string('x', 75)).Append('\n');

        dir.WriteFile("wide.txt", sb.ToString());
        var tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"wide.txt"}"""));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content.Length <= 64 * 1024, Is.True);
        Assert.That(result.Content, Does.Contain("00808"));
        Assert.That(result.Content, Does.Not.Contain("00809")); // 第 809 行被完整跳过
        Assert.That(result.Content, Does.Contain("truncated before line 809; continue with offset=809"));
    }

    [Test]
    public async Task ReadFile_LineCut_DoesNotSplitSurrogatePair()
    {
        // 8 KiB 截断点落在 emoji 两个代码单元之间：截断必须保持代理对完整，
        // 不能输出孤立代理项。
        using var dir = new TestTempDir();
        dir.WriteFile("emoji.txt", new string('a', 8191) + "\U0001F600" + new string('b', 10) + "\n");
        var tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"emoji.txt"}"""));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.StartWith(new string('a', 8191) + "\U0001F600"));
        Assert.That(result.Content, Does.Not.Contain(new string('a', 8192)));
    }

    [Test]
    public async Task ReadFile_FileOverMaxBytes_IsRefusedNotRead()
    {
        using var dir = new TestTempDir();
        dir.WriteBytes("huge.txt", new byte[4 * 1024 * 1024 + 1]);
        var tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"huge.txt"}"""));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.Contain("4 MiB read limit"));
        Assert.That(result.Content, Does.Contain("search_text"));
    }

    [Test]
    public async Task ReadFile_FileJustUnderMaxBytes_IsReadNotRefused()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("edge.txt", new string('x', 4 * 1024 * 1024 - 64) + "\n");
        var tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"edge.txt"}"""));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.Not.Contain("read limit"));
        Assert.That(result.Content, Does.Contain("[line 1 truncated]"));
    }

    [Test]
    public async Task ReadFile_BinaryFile_IsReportedNotDumped()
    {
        using var dir = new TestTempDir();
        dir.WriteBytes("blob.dat", [0x50, 0x4B, 0x00, 0x01, 0x02]);
        var tool = new ReadFileTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"blob.dat"}"""));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.Contain("binary file"));
    }

    [Test]
    public void ReadFile_Prepare_RejectsMissingPath()
    {
        using var dir  = new TestTempDir();
        var       tool = new ReadFileTool(dir.Workspace);

        var ex = TestAssert.Throws<InvalidDataException>(() => Prepare(tool, "{}"));
        Assert.That(ex.Message, Does.Contain("'path'"));
    }

    [Test]
    public void ReadFile_Prepare_RejectsEscape()
    {
        using var dir  = new TestTempDir();
        var       tool = new ReadFileTool(dir.Workspace);

        TestAssert.Throws<InvalidDataException>(() => Prepare(tool, """{"path":"../secret.txt"}"""));
    }

    [Test]
    public void ReadFile_Prepare_RejectsInvalidOffset()
    {
        using var dir  = new TestTempDir();
        var       tool = new ReadFileTool(dir.Workspace);

        TestAssert.Throws<InvalidDataException>(() => Prepare(tool, """{"path":"a.txt","offset":0}"""));
    }

    // ---- list_files --------------------------------------------------------

    [Test]
    public async Task ListFiles_ListsTopLevelEntries()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("README.md", "x");
        dir.WriteFile("src/a.cs", "x");
        var tool = new ListFilesTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, "{}"));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.Contain("README.md"));
        Assert.That(result.Content, Does.Contain("src/"));
        Assert.That(result.Content, Does.Not.Contain("src/a.cs"));
    }

    [Test]
    public async Task ListFiles_Recursive_ShowsNestedFiles()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("src/deep/a.cs", "x");
        var tool = new ListFilesTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"recursive":true}"""));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.Contain("src/deep/a.cs"));
    }

    [Test]
    public async Task ListFiles_OmitsBuildAndVcDirectories()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("obj/junk.cs", "x");
        dir.WriteFile(".git/config", "x");
        dir.WriteFile("src/real.cs", "x");
        var tool = new ListFilesTool(dir.Workspace);

        var recursive = await ExecuteAsync(tool, Prepare(tool, """{"recursive":true}"""));

        Assert.That(recursive.Content, Does.Contain("src/real.cs"));
        Assert.That(recursive.Content, Does.Not.Contain("obj/"));
        Assert.That(recursive.Content, Does.Not.Contain(".git/"));
        Assert.That(recursive.Content, Does.Not.Contain("junk.cs"));

        // 显式请求被排除的目录时仍可访问。
        var explicitResult = await ExecuteAsync(tool, Prepare(tool, """{"path":"obj"}"""));
        Assert.That(explicitResult.Content, Does.Contain("junk.cs"));
    }

    [Test]
    public async Task ListFiles_MissingDirectory_ReturnsNotFound()
    {
        using var dir  = new TestTempDir();
        var       tool = new ListFilesTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"path":"nope"}"""));

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.Content, Does.Contain("Directory not found"));
    }

    [Test]
    public async Task ListFiles_MaxDepth_LimitsRecursion()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("src/a.cs", "x");
        dir.WriteFile("src/deep/b.cs", "x");
        dir.WriteFile("src/deep/deeper/c.cs", "x");
        var tool = new ListFilesTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"recursive":true,"maxDepth":1}"""));

        Assert.That(result.Content, Does.Contain("src/a.cs"));
        Assert.That(result.Content, Does.Not.Contain("src/deep/b.cs"));
    }

    // ---- search_text -------------------------------------------------------

    [Test]
    public async Task SearchText_FindsAcrossFiles_IgnoreCaseByDefault()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("a.txt", "The quick brown fox\njumps over\n");
        dir.WriteFile("b.txt", "nothing here\nquick AND dirty\n");
        var tool = new SearchTextTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"pattern":"QUICK"}"""));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.Contain("a.txt:1: The quick brown fox"));
        Assert.That(result.Content, Does.Contain("b.txt:2: quick AND dirty"));
    }

    [Test]
    public async Task SearchText_CaseSensitive_Distinguishes()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("a.txt", "Quick\nquick\n");
        var tool = new SearchTextTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"pattern":"quick","caseSensitive":true}"""));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.Contain("a.txt:2: quick"));
        Assert.That(result.Content, Does.Not.Contain("a.txt:1"));
    }

    [Test]
    public async Task SearchText_StopsAtMaxResults()
    {
        using var dir   = new TestTempDir();
        var       lines = string.Concat(Enumerable.Range(1, 300).Select(i => $"needle {i}\n"));
        dir.WriteFile("haystack.txt", lines);
        var tool = new SearchTextTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"pattern":"needle","maxResults":50}"""));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.Contain("stopped after 50 matches"));
    }

    [Test]
    public async Task SearchText_NoMatches_ReportsCleanly()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("a.txt", "hello\n");
        var tool = new SearchTextTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"pattern":"zzz"}"""));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.Contain("no matches"));
    }

    [Test]
    public async Task SearchText_SkipsBinaryFiles()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("a.txt", "needle in text\n");
        dir.WriteBytes("b.bin", [0x00, 0x01, 0x02, .. "needle"u8.ToArray()]);
        var tool = new SearchTextTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"pattern":"needle"}"""));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.Contain("a.txt:1: needle in text"));
        Assert.That(result.Content, Does.Not.Contain("b.bin:1"));
    }

    [Test]
    public async Task SearchText_WalkOverFileCap_ReportsPartialScan()
    {
        // 文件数超过遍历上限：结果必须说明扫描不完整，而不是给出 "(no matches...)"、
        // 假装已看过整个仓库。
        using var dir = new TestTempDir();
        for (var i = 0; i < 3_005; i++) dir.WriteFile($"f{i:D4}.txt", "filler\n");

        var tool = new SearchTextTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"pattern":"needle-partial-scan"}"""));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.Contain("no matches"));
        Assert.That(result.Content, Does.Contain("only the first 3000 files scanned"));
    }

    [Test]
    public async Task SearchText_FileSymlinkPointingOutside_IsSkippedNotRead()
    {
        // 工作区内目标在工作区外的文件符号链接：其内容不得被读取或展示（PLAN §11）。
        using var workspaceDir = new TestTempDir();
        using var outsideDir   = new TestTempDir();
        var       outsideFile  = outsideDir.WriteFile("secret.txt", "TOP-SECRET-PLACEHOLDER needle-outside\n");
        var       linkPath     = Path.Combine(workspaceDir.Root, "link.txt");
        if (!TryCreateFileSymlink(linkPath, outsideFile)) return; // 无符号链接权限；无可验证内容。

        var tool = new SearchTextTool(workspaceDir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"pattern":"needle-outside"}"""));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.Not.Contain("TOP-SECRET-PLACEHOLDER"));
        Assert.That(result.Content, Does.Contain("file link(s) to outside the workspace"));
    }

    [Test]
    public async Task SearchText_FileSymlinkInsideWorkspace_IsFollowed()
    {
        // 最终目标仍在工作区内的文件符号链接可被搜索：边界规则针对解析后的目标而非
        // 链接本身（与 read_file 语义一致）。
        using var dir      = new TestTempDir();
        var       target   = dir.WriteFile("real.txt", "needle-inside\n");
        var       linkPath = Path.Combine(dir.Root, "alias.txt");
        if (!TryCreateFileSymlink(linkPath, target)) return; // 无符号链接权限；无可验证内容。

        var tool = new SearchTextTool(dir.Workspace);

        var result = await ExecuteAsync(tool, Prepare(tool, """{"pattern":"needle-inside"}"""));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.Contain("alias.txt:1: needle-inside"));
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
            return false; // 无符号链接权限（开发者模式/管理员）；跳过。
        }
    }

    [Test]
    public void SearchText_Prepare_RejectsEmptyPattern()
    {
        using var dir  = new TestTempDir();
        var       tool = new SearchTextTool(dir.Workspace);

        TestAssert.Throws<InvalidDataException>(() => Prepare(tool, """{"pattern":"  "}"""));
    }

    [Test]
    public void SearchText_Prepare_RejectsEscape()
    {
        using var dir  = new TestTempDir();
        var       tool = new SearchTextTool(dir.Workspace);

        TestAssert.Throws<InvalidDataException>(() => Prepare(tool, """{"pattern":"x","path":"../outside"}"""));
    }

    // ---- Agent Loop over real read-only tools -----------------------------

    [Test]
    public async Task AgentLoop_ReadToolResult_FeedsNextModelTurn()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("data.txt", "alpha\nbeta\n");
        var client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"data.txt"}"""));
        client.Enqueue(FakeChatClient.Text("I read the file."));
        var loop = new AgentLoop(client, new ToolRegistry([new ReadFileTool(dir.Workspace)]), Options());

        var result = await loop.RunAsync("sys", "read data.txt", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        Assert.That(client.Requests, Is.EqualTo(2));
        TestAssert.ContainsAll(m => m is { Role: ChatRole.Tool, Content: "alpha\nbeta" }, loop.History);
    }

    [Test]
    public async Task AgentLoop_ReadFailure_IsReportedBackAndLoopContinues()
    {
        using var dir    = new TestTempDir();
        var       client = new FakeChatClient();
        client.Enqueue(FakeChatClient.ToolCall("read_file", """{"path":"missing.txt"}"""));
        client.Enqueue(FakeChatClient.Text("cannot read"));
        var loop = new AgentLoop(client, new ToolRegistry([new ReadFileTool(dir.Workspace)]), Options());

        var result = await loop.RunAsync("sys", "read missing.txt", CancellationToken.None);

        Assert.That(result.Status, Is.EqualTo(AgentStatus.Completed));
        TestAssert.ContainsAll(m => m.Role == ChatRole.Tool && m.Content.Contains("File not found"), loop.History);
    }

    private static AgentOptions Options() => new()
    {
        Model                     = "test-model",
        MaxAgentSteps             = 10,
        DefaultToolTimeoutSeconds = 30
    };
}
