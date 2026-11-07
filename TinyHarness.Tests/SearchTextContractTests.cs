using System.Text.RegularExpressions;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         search_text 契约（D8）测试：literal 缺省兼容、regex 子集与显式报错、glob 过滤不扩权、
///         上下文合并、三种输出模式、预算与部分结果标注、策略排除文件不泄露。
///     </para>
///     <para>
///         Contract tests for search_text (D8): literal default compatibility, the regex subset and explicit
///         errors, non-widening glob filters, context merging, the three output modes, budget and partial-result
///         marking, and no leakage of policy-excluded files.
///     </para>
/// </summary>
public class SearchTextContractTests
{
    private static ChatToolCall Call(ITool tool, string argumentsJson) =>
        new("call_1", tool.Definition.Name, argumentsJson);

    private static ToolPreparation Prepare(ITool tool, string argumentsJson) => tool.Prepare(Call(tool, argumentsJson));

    private static async Task<ToolResult> RunAsync(ITool tool, string argumentsJson)
    {
        var preparation = Prepare(tool, argumentsJson);
        return await tool.ExecuteAsync(preparation, CancellationToken.None);
    }

    // ---- literal 缺省行为不变 ---------------------------------------------

    [Test]
    public async Task LiteralExplicitMode_MatchesDefaultBehavior()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("a.txt", "alpha beta\nnone\n");
        var tool = new SearchTextTool(dir.Workspace);

        var explicitMode = await RunAsync(tool, """{"pattern":"alpha","mode":"literal"}""");
        var defaultMode  = await RunAsync(tool, """{"pattern":"alpha"}""");

        Assert.That(explicitMode.Succeeded, Is.True);
        Assert.That(explicitMode.Content, Is.EqualTo(defaultMode.Content));
        Assert.That(explicitMode.Content, Does.Contain("a.txt:1: alpha beta"));
    }

    // ---- regex 模式 -------------------------------------------------------

    [Test]
    public async Task RegexMode_MatchesPerLine()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("nums.txt", "run 42\nno digits here\nrun 7 once more\n");
        var tool = new SearchTextTool(dir.Workspace);

        var result = await RunAsync(tool, """{"pattern":"run \\d+","mode":"regex"}""");

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.Contain("nums.txt:1: run 42"));
        Assert.That(result.Content, Does.Contain("nums.txt:3: run 7 once more"));
        Assert.That(result.Content, Does.Not.Contain("no digits"));
    }

    [Test]
    public void RegexMode_UnsupportedSyntax_FailsPreparationWithoutExecuting()
    {
        using var dir  = new TestTempDir();
        var       tool = new SearchTextTool(dir.Workspace);

        // 环视是 NonBacktracking 明确不支持的语法：准备期报错，不静默换引擎。
        var ex = Assert.Throws<InvalidDataException>(
            () => Prepare(tool, """{"pattern":"foo(?=bar)","mode":"regex"}"""));
        Assert.That(ex!.Message, Does.Contain("NonBacktracking"));
    }

    [Test]
    public void RegexMode_PatternOver512Characters_FailsPreparation()
    {
        using var dir  = new TestTempDir();
        var       tool = new SearchTextTool(dir.Workspace);

        var pattern = new string('a', 513);
        TestAssert.Throws<InvalidDataException>(
            () => Prepare(tool, $$"""{"pattern":"{{pattern}}","mode":"regex"}"""));
    }

    [Test]
    public void RegexMode_InvalidSyntax_FailsPreparationWithoutExecuting()
    {
        using var dir  = new TestTempDir();
        var       tool = new SearchTextTool(dir.Workspace);

        // 纯语法错误（未闭合分组）由 RegexParseException 报告，同样转换为明确准备错误。
        var ex = Assert.Throws<InvalidDataException>(
            () => Prepare(tool, """{"pattern":"ab(c","mode":"regex"}"""));
        Assert.That(ex!.Message, Does.Contain("NonBacktracking"));
    }

    [Test]
    public async Task UnicodeAndChinesePatterns_MatchPerLine()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("zh.txt", "这是中文内容\nEnglish line\n数字１２３\n");
        var tool = new SearchTextTool(dir.Workspace);

        var literal = await RunAsync(tool, """{"pattern":"中文内容"}""");
        var regex   = await RunAsync(tool, """{"pattern":"\\p{IsCJKUnifiedIdeographs}+","mode":"regex"}""");

        Assert.That(literal.Succeeded, Is.True);
        Assert.That(literal.Content, Does.Contain("zh.txt:1: 这是中文内容"));
        Assert.That(regex.Succeeded, Is.True);
        Assert.That(regex.Content, Does.Contain("zh.txt:1: 这是中文内容"));
        Assert.That(regex.Content, Does.Not.Contain("English line"));
    }

    [Test]
    public void RegexMode_UnknownModeValue_FailsPreparation()
    {
        using var dir  = new TestTempDir();
        var       tool = new SearchTextTool(dir.Workspace);

        TestAssert.Throws<InvalidDataException>(
            () => Prepare(tool, """{"pattern":"x","mode":"fuzzy"}"""));
    }

    [Test]
    public async Task RegexMode_CaseSensitiveToggle_IgnoreCaseFollows()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("case.txt", "Needle\nneedle\nNEEDLE\n");
        var tool = new SearchTextTool(dir.Workspace);

        var insensitive = await RunAsync(tool, """{"pattern":"needle","mode":"regex"}""");
        var sensitive   = await RunAsync(tool, """{"pattern":"needle","mode":"regex","caseSensitive":true}""");

        Assert.That(insensitive.Content, Does.Contain("case.txt:1"));
        Assert.That(insensitive.Content, Does.Contain("case.txt:2"));
        Assert.That(insensitive.Content, Does.Contain("case.txt:3"));
        Assert.That(sensitive.Content, Does.Contain("case.txt:2"));
        Assert.That(sensitive.Content, Does.Not.Contain("case.txt:1"));
    }

    // ---- include/exclude glob：只过滤已授权扫描集 ------------------------

    [Test]
    public async Task IncludeGlob_NarrowsScanSet()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("src/keep.cs", "needle in cs\n");
        dir.WriteFile("docs/drop.md", "needle in md\n");
        var tool = new SearchTextTool(dir.Workspace);

        var result = await RunAsync(tool, """{"pattern":"needle","include":["**/*.cs"]}""");

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.Contain("src/keep.cs:1"));
        Assert.That(result.Content, Does.Not.Contain("drop.md"));
        Assert.That(result.Content, Does.Contain("skipped 1 file(s) by include/exclude filters"));
    }

    [Test]
    public async Task ExcludeGlob_RemovesMatchingFilesAndWinsOverInclude()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("src/a.cs", "needle a\n");
        dir.WriteFile("src/a.gen.cs", "needle b\n");
        var tool = new SearchTextTool(dir.Workspace);

        var result = await RunAsync(tool,
                                    """{"pattern":"needle","include":["**/*.cs"],"exclude":["**/*.gen.cs"]}""");

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.Contain("src/a.cs:1"));
        Assert.That(result.Content, Does.Not.Contain("a.gen.cs"));
    }

    [Test]
    public async Task Glob_QuestionMarkAndCharClass_Match()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("log1.txt", "needle one\n");
        dir.WriteFile("log2.txt", "needle two\n");
        dir.WriteFile("logX.txt", "needle x\n");
        var tool = new SearchTextTool(dir.Workspace);

        var result = await RunAsync(tool, """{"pattern":"needle","include":["log[12].txt"]}""");

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.Contain("log1.txt"));
        Assert.That(result.Content, Does.Contain("log2.txt"));
        Assert.That(result.Content, Does.Not.Contain("logX.txt"));
    }

    [Test]
    public async Task Glob_IsCaseInsensitiveByDefaultAndCaseSensitiveWhenAsked()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("UPPER.TXT", "needle upper\n");
        dir.WriteFile("lower.txt", "needle lower\n");
        var tool = new SearchTextTool(dir.Workspace);

        var insensitive = await RunAsync(tool, """{"pattern":"needle","include":["upper.txt"]}""");
        var sensitive   = await RunAsync(tool, """{"pattern":"needle","include":["UPPER.TXT"],"caseSensitive":true}""");

        Assert.That(insensitive.Content, Does.Contain("UPPER.TXT"));
        Assert.That(sensitive.Content, Does.Contain("UPPER.TXT"));
        Assert.That(sensitive.Content, Does.Not.Contain("lower.txt"));
    }

    [Test]
    public void Glob_BlankPattern_FailsPreparation()
    {
        using var dir  = new TestTempDir();
        var       tool = new SearchTextTool(dir.Workspace);

        TestAssert.Throws<InvalidDataException>(
            () => Prepare(tool, """{"pattern":"x","include":["  "]}"""));
    }

    // ---- 上下文合并 -------------------------------------------------------

    [Test]
    public async Task ContextLines_MergeAdjacentWindowsAndMarkMatches()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("ctx.txt",
                      "l1\nl2\nhit one\nl4\nl5\nl6\nhit two\nl8\nl9\nl10\nl11\n");
        var tool = new SearchTextTool(dir.Workspace);

        var result = await RunAsync(tool, """{"pattern":"hit","contextBefore":2,"contextAfter":1}""");

        Assert.That(result.Succeeded, Is.True);
        // 窗口 [1-4] 与 [4-8] 相邻合并为 [1-8]；> 标记匹配行，块间以 -- 分隔。
        Assert.That(result.Content, Does.Contain("ctx.txt:1-8:"));
        Assert.That(result.Content, Does.Contain("> 3: hit one"));
        Assert.That(result.Content, Does.Contain("> 7: hit two"));
        Assert.That(result.Content, Does.Contain("  1: l1"));
        Assert.That(result.Content, Does.Contain("--"));
        Assert.That(result.Content, Does.Not.Contain("l11"));
    }

    [Test]
    public void ContextLines_OutOfRange_FailsPreparation()
    {
        using var dir  = new TestTempDir();
        var       tool = new SearchTextTool(dir.Workspace);

        TestAssert.Throws<InvalidDataException>(
            () => Prepare(tool, """{"pattern":"x","contextBefore":6}"""));
        TestAssert.Throws<InvalidDataException>(
            () => Prepare(tool, """{"pattern":"x","contextAfter":-1}"""));
    }

    // ---- 输出模式 ---------------------------------------------------------

    [Test]
    public async Task OutputModeFiles_ListsPathOnlyWithoutContent()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("f1.txt", "secret-content\n");
        dir.WriteFile("f2.txt", "nothing\n");
        var tool = new SearchTextTool(dir.Workspace);

        var result = await RunAsync(tool, """{"pattern":"secret","outputMode":"files"}""");

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.Contain("f1.txt"));
        Assert.That(result.Content, Does.Not.Contain("secret-content"));
        Assert.That(result.Content, Does.Not.Contain("f2.txt"));
        Assert.That(result.Content, Does.Contain("1 file with matches"));
    }

    [Test]
    public async Task OutputModeCount_ReportsPerFileCounts()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("c1.txt", "w\nw\nw\n");
        dir.WriteFile("c2.txt", "w\n");
        dir.WriteFile("c3.txt", "none\n");
        var tool = new SearchTextTool(dir.Workspace);

        var result = await RunAsync(tool, """{"pattern":"w","outputMode":"count"}""");

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.Contain("c1.txt: 3"));
        Assert.That(result.Content, Does.Contain("c2.txt: 1"));
        Assert.That(result.Content, Does.Not.Contain("c3.txt"));
    }

    [Test]
    public async Task OutputModeCount_PartialCountIsExplicitlyMarked()
    {
        using var dir   = new TestTempDir();
        var       lines = string.Concat(Enumerable.Range(1, 300).Select(i => $"needle {i}\n"));
        dir.WriteFile("big.txt", lines);
        var tool = new SearchTextTool(dir.Workspace);

        var result = await RunAsync(tool, """{"pattern":"needle","outputMode":"count","maxResults":10}""");

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.Contain("big.txt: 10"));
        Assert.That(result.Content, Does.Contain("stopped after 10 matches; results are partial"));
    }

    // ---- 输出预算与部分标注 ------------------------------------------------

    [Test]
    public async Task OutputBudgetExceeded_TruncatesWithExplicitMarker()
    {
        using var dir  = new TestTempDir();
        // 每行接近 240 列显上限：500 个匹配远超 64k 输出预算。
        var filler = new string('x', 236);
        var lines  = string.Concat(Enumerable.Range(1, 600).Select(i => $"needle {i} {filler}\n"));
        dir.WriteFile("huge.txt", lines);
        var tool = new SearchTextTool(dir.Workspace);

        var result = await RunAsync(tool, """{"pattern":"needle","maxResults":500}""");

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.Contain("output truncated at 65536 characters; results are partial"));
        Assert.That(result.Content.Length, Is.LessThan(70_000));
    }

    [Test]
    public async Task PartialScanWithNoMatches_IsNeverReportedAsAuthoritativeNoMatch()
    {
        // 文件数超上限且无匹配：不得显示为权威"无匹配"，必须连同部分扫描事实说明。
        using var dir = new TestTempDir();
        for (var i = 0; i < 3_005; i++) dir.WriteFile($"f{i:D4}.txt", "filler\n");

        var tool = new SearchTextTool(dir.Workspace);

        var result = await RunAsync(tool, """{"pattern":"needle-partial","include":["*.txt"]}""");

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.Contain("no matches within scanned limits; results are partial"));
        Assert.That(result.Content, Does.Contain("only the first 3000 files scanned"));
    }

    [Test]
    public async Task FullScanWithNoMatches_KeepsCleanNoMatchText()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("a.txt", "hello\n");
        var tool = new SearchTextTool(dir.Workspace);

        var result = await RunAsync(tool, """{"pattern":"zzz"}""");

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.Contain("(no matches for 'zzz')"));
    }

    [Test]
    public async Task RegexCompilationAndExecute_AgreeOnTimeoutBudget()
    {
        // NonBacktracking 引擎构造必须携带 MatchTimeout；本测试确认合法 pattern 在预算内完成，
        // 超时路径按代码审查覆盖（线性引擎难以确定性触发）。
        using var dir = new TestTempDir();
        dir.WriteFile("rx.txt", "abcabcabc\n");
        var tool = new SearchTextTool(dir.Workspace);

        var result = await RunAsync(tool, """{"pattern":"(abc)+","mode":"regex"}""");

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Content, Does.Contain("rx.txt:1: abcabcabc"));
    }
}
