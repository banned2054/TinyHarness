using System.Text.Json;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         C2 递归入口审批覆盖：普通父目录的基线广泛读不得隐式开放敏感后代——递归搜索与递归列举
///         在遍历入口排除未获批的敏感命中（计数、绝不露名、绝不读内容），扫描期二次复核覆盖
///         Prepare 与 Execute 之间的新增敏感命中；本次准备档即敏感档（搜索根/列举根/显式目标
///         走了 L2 审批）时不收缩已批准范围。全部使用合成敏感文件与独立临时目录。
///     </para>
///     <para>
///         C2 approval coverage for recursive entries: a normal parent's baseline broad read never
///         implicitly opens sensitive descendants — recursive search and recursive listing exclude
///         unapproved sensitive hits at the walk entry (counted, never named, never read), and a
///         scan-time re-check covers sensitive hits created between Prepare and Execute; when the
///         invocation's prepared strictest class is the sensitive one (the root or explicit target
///         went through L2 approval), an approved scope is never narrowed. Everything runs on
///         synthetic sensitive files inside disposable directories.
///     </para>
/// </summary>
public class SensitiveRecursiveEntryApprovalTests
{
    private static ChatToolCall Call(ITool tool, string argumentsJson) =>
        new("call_1", tool.Definition.Name, argumentsJson);

    private static ToolPreparation Prepare(ITool tool, string argumentsJson) => tool.Prepare(Call(tool, argumentsJson));

    /// <summary>
    ///     合成敏感布局：普通目录下放普通文件、.env、.ssh/id_rsa 与普通子树。 A synthetic sensitive layout: normal files beside .env, .ssh/id_rsa,
    ///     and a normal subtree.
    /// </summary>
    private static void WriteSensitiveLayout(TestTempDir dir)
    {
        dir.WriteFile("outside/notes.txt", "ordinary needle here\n");
        dir.WriteFile("outside/app.log", "nothing to see\n");
        dir.WriteFile("outside/.env", "SECRET_TOKEN=hunter2 needle\n");
        dir.WriteFile("outside/.ssh/id_rsa", "PRIVATE KEY MATERIAL\n");
        dir.WriteFile("outside/project/code.cs", "another needle\n");
    }

    private static (SearchTextTool Tool, PermissionEngine Engine, string Root) CreateSearchFixture(TestTempDir dir)
    {
        var workspaceRoot = Path.Combine(dir.Root, "ws");
        Directory.CreateDirectory(workspaceRoot);
        var policy    = new ResourceAccessPolicy(workspaceRoot);
        var workspace = new Workspace(workspaceRoot, true);
        return (new SearchTextTool(workspace, resourcePolicy : policy),
                new PermissionEngine(workspaceRoot, resourcePolicy : policy),
                Path.Combine(dir.Root, "outside"));
    }

    [Test]
    public async Task RecursiveSearch_ExcludesUnapprovedSensitiveDescendantsWithoutLeakingNames()
    {
        using var dir = new TestTempDir();
        WriteSensitiveLayout(dir);
        var (tool, _, root) = CreateSearchFixture(dir);

        var preparation = Prepare(tool, $$"""{"pattern":"needle","path":{{ToolJson(root)}}}""");
        var result      = await tool.ExecuteAsync(preparation, CancellationToken.None);

        Assert.Multiple(() =>
        {
            // 普通文件正常命中。
            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Content, Does.Contain("notes.txt"));
            Assert.That(result.Content, Does.Contain("code.cs"));

            // 敏感后代的内容与名字都不出现；只留跳过计数。
            Assert.That(result.Content, Does.Not.Contain("SECRET_TOKEN"));
            Assert.That(result.Content, Does.Not.Contain("PRIVATE KEY"));
            Assert.That(result.Content, Does.Not.Contain(".env"));
            Assert.That(result.Content, Does.Not.Contain("id_rsa"));
            Assert.That(result.Content, Does.Contain("sensitive locations that were not approved"));
        });
    }

    [Test]
    public void RecursiveSearch_NormalRoot_EvaluatesLevel1ButSensitiveReadsStillNeedApproval()
    {
        using var dir = new TestTempDir();
        WriteSensitiveLayout(dir);
        var (tool, engine, root) = CreateSearchFixture(dir);

        // 搜索根本身是普通外部目录：按 L1 广泛读基线自动放行。
        var search = engine.Evaluate(Prepare(tool, $$"""{"pattern":"needle","path":{{ToolJson(root)}}}"""));
        Assert.Multiple(() =>
        {
            Assert.That(search.Decision, Is.EqualTo(PermissionDecision.Allow));
            Assert.That(search.Level, Is.EqualTo(PermissionLevel.Level1));
        });
    }

    [Test]
    public async Task RecursiveSearch_ApprovedSensitiveRoot_ScansDescendantsInsideApprovedScope()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("outside/.ssh/allowed.txt", "approved needle\n");
        var (tool, _, _) = CreateSearchFixture(dir);
        var sensitiveRoot = Path.Combine(dir.Root, "outside", ".ssh");

        // 根即敏感位置：准备档严格档为敏感档（引擎按 L2 审批），执行期的覆盖判定不得收缩已批准范围。
        var preparation = Prepare(tool, $$"""{"pattern":"needle","path":{{ToolJson(sensitiveRoot)}}}""");
        var result      = await tool.ExecuteAsync(preparation, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Content, Does.Contain("approved needle"));
            Assert.That(result.Content, Does.Not.Contain("sensitive locations that were not approved"));
        });
    }

    [Test]
    public async Task RecursiveListing_ExcludesUnapprovedSensitiveEntriesWithCountOnly()
    {
        using var dir = new TestTempDir();
        WriteSensitiveLayout(dir);
        var workspaceRoot = Path.Combine(dir.Root, "ws");
        Directory.CreateDirectory(workspaceRoot);
        var policy    = new ResourceAccessPolicy(workspaceRoot);
        var workspace = new Workspace(workspaceRoot, true);
        var tool      = new ListFilesTool(workspace, resourcePolicy : policy);

        var preparation =
            Prepare(tool, $$"""{"path":{{ToolJson(Path.Combine(dir.Root, "outside"))}},"recursive":true}""");
        var result = await tool.ExecuteAsync(preparation, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Content, Does.Contain("notes.txt"));
            Assert.That(result.Content, Does.Contain("project/"));
            Assert.That(result.Content, Does.Not.Contain(".env"));
            Assert.That(result.Content, Does.Not.Contain(".ssh"));
            Assert.That(result.Content, Does.Contain("sensitive locations that were not approved"));
        });
    }

    [Test]
    public async Task SensitiveReadTarget_ReachesApprovalAndGrantStillExecutes()
    {
        using var dir = new TestTempDir();
        WriteSensitiveLayout(dir);
        var workspaceRoot = Path.Combine(dir.Root, "ws");
        Directory.CreateDirectory(workspaceRoot);
        var policy    = new ResourceAccessPolicy(workspaceRoot);
        var workspace = new Workspace(workspaceRoot, true);
        var tool      = new ReadFileTool(workspace, resourcePolicy : policy);
        var engine    = new PermissionEngine(workspaceRoot, resourcePolicy : policy);
        var sensitive = Path.Combine(dir.Root, "outside", ".env");

        // 敏感显式目标按 L2 逐次审批（读能力），未获批不执行。
        var preparation = Prepare(tool, $$"""{"path":{{ToolJson(sensitive)}}}""");
        var verdict     = engine.Evaluate(preparation);
        Assert.Multiple(() =>
        {
            Assert.That(verdict.Decision, Is.EqualTo(PermissionDecision.Ask));
            Assert.That(verdict.Level, Is.EqualTo(PermissionLevel.Level2));
            Assert.That(verdict.MissingCapabilities, Does.Contain("additional-path-read"));
        });

        // Turn 授权覆盖同一敏感目标后放行：已批准范围不被执行期覆盖判定收缩。
        engine.Grants.Add(GrantScope.Turn, preparation, "additional-path-read");
        var granted = engine.Evaluate(preparation);
        Assert.That(granted.Decision, Is.EqualTo(PermissionDecision.Allow));

        var result = await tool.ExecuteAsync(preparation, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Content, Does.Contain("SECRET_TOKEN"));
        });
    }

    [Test]
    public async Task InsideWorkspaceSensitiveNames_StayBaselineAndRemainSearchable()
    {
        using var dir = new TestTempDir();
        WriteSensitiveLayout(dir);
        // 工作区内敏感名不自动识别：基线行为不变，搜索照常命中。
        var workspaceRoot = Path.Combine(dir.Root, "ws");
        Directory.CreateDirectory(workspaceRoot);
        File.WriteAllText(Path.Combine(workspaceRoot, ".env"), "workspace secret needle\n");
        var policy = new ResourceAccessPolicy(workspaceRoot);
        var tool   = new SearchTextTool(new Workspace(workspaceRoot), resourcePolicy : policy);

        var result = await tool.ExecuteAsync(Prepare(tool, """{"pattern":"needle"}"""), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Content, Does.Contain(".env"));
            Assert.That(result.Content, Does.Not.Contain("sensitive locations that were not approved"));
        });
    }

    [Test]
    public void AdditionalReadRootSensitiveNames_StayOnGroupChannelWithoutEscalation()
    {
        using var dir = new TestTempDir();
        WriteSensitiveLayout(dir);
        var workspaceRoot = Path.Combine(dir.Root, "ws");
        Directory.CreateDirectory(workspaceRoot);
        var readRoot = Path.Combine(dir.Root, "outside");
        var policy   = new ResourceAccessPolicy(workspaceRoot, [readRoot]);
        var tool     = new SearchTextTool(new Workspace(workspaceRoot, true), resourcePolicy : policy);
        var engine   = new PermissionEngine(workspaceRoot, resourcePolicy : policy);

        // 附加读根内命中敏感名不升档（用户显式信任面），执行期覆盖判定不得把该根的后代判为未获批。
        var preparation = Prepare(tool, $$"""{"pattern":"needle","path":{{ToolJson(readRoot)}}}""");
        var verdict     = engine.Evaluate(preparation);
        Assert.That(verdict.Decision, Is.EqualTo(PermissionDecision.Allow));

        var result = tool.ExecuteAsync(preparation, CancellationToken.None).GetAwaiter().GetResult();
        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Content, Does.Contain("SECRET_TOKEN"));
            Assert.That(result.Content, Does.Not.Contain("sensitive locations that were not approved"));
        });
    }

    /// <summary>把绝对路径包成 JSON 字符串字面量供参数 JSON 使用。 Wraps an absolute path as a JSON string literal for argument JSON.</summary>
    private static string ToolJson(string path) => JsonSerializer.Serialize(path);
}
