using System.Diagnostics;
using System.Text.Json;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.ProjectInstructions;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.ProjectInstructions;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     统一资源访问策略的分类矩阵与文件工具接入测试：基线、附加根、受保护元数据、
///     权限系统保护路径、工作区外与链接改向，以及 Prepare 硬拒与注记传播。
///     Classification-matrix and file-tool integration tests for the unified resource
///     access policy: baseline, additional roots, protected metadata,
///     permission-system protected paths, outside workspace, and link redirection,
///     plus the Prepare-side hard denial and annotation propagation.
/// </summary>
public class ResourceAccessPolicyTests
{
    // ---------------------------------------------------------------- 分类矩阵

    [Test]
    public void Classify_BaselineWorkspace_ForPlainWorkspaceFile()
    {
        using var dir    = new TestTempDir();
        var       file   = dir.WriteFile("src/Program.cs", "x");
        var       policy = new ResourceAccessPolicy(dir.Root);

        var classification = policy.Classify(file);

        Assert.That(classification.Class, Is.EqualTo(ResourceClass.BaselineWorkspace));
        Assert.That(classification.KnownSensitive, Is.False);
    }

    [Test]
    public void Classify_ProtectedMetadata_ForDefaultMetadataSubdirectories()
    {
        using var dir    = new TestTempDir();
        var       policy = new ResourceAccessPolicy(dir.Root);

        foreach (var name in new[] { ".git", ".agents", ".codex", ".aws" })
        {
            dir.CreateDirectory(name);
            var target = dir.WriteFile($"{name}/config", "x");

            Assert.That(policy.Classify(target).Class, Is.EqualTo(ResourceClass.ProtectedMetadata), name);
            Assert.That(policy.Classify(Path.Combine(dir.Root, name)).Class,
                        Is.EqualTo(ResourceClass.ProtectedMetadata), name);
        }
    }

    [Test]
    public void Classify_PermissionSystemProtected_ForExplicitProtectedRootsInsideAndOutsideWorkspace()
    {
        using var workspaceDir = new TestTempDir();
        using var configDir    = new TestTempDir();
        var outsideConfig = configDir.WriteFile("user-config.json", "{}");
        var insideStore   = workspaceDir.WriteFile(".store/session.jsonl", "{}");
        // 文件保护根精确匹配该文件；目录保护根按等值或包含匹配。
        var policy = new ResourceAccessPolicy(workspaceDir.Root,
                                              permissionSystemRoots : [outsideConfig,
                                                                       configDir.Root,
                                                                       Path.Combine(workspaceDir.Root, ".store")]);

        Assert.That(policy.Classify(outsideConfig).Class, Is.EqualTo(ResourceClass.PermissionSystemProtected));
        Assert.That(policy.Classify(insideStore).Class, Is.EqualTo(ResourceClass.PermissionSystemProtected));
        // 目录保护根本身（等值）与其中子路径（包含）都是保护目标。
        Assert.That(policy.Classify(configDir.Root).Class, Is.EqualTo(ResourceClass.PermissionSystemProtected));
        Assert.That(policy.Classify(Path.Combine(configDir.Root, "nested.txt")).Class,
                    Is.EqualTo(ResourceClass.PermissionSystemProtected));
    }

    [Test]
    public void Classify_PermissionSystemProtected_OverridesMetadataAndBaseline()
    {
        using var dir = new TestTempDir();
        dir.CreateDirectory(".git");
        var target = dir.WriteFile(".git/config", "x");
        // 权限系统保护语义永远优先：保护根与元数据根重叠时按保护类判定。
        var policy = new ResourceAccessPolicy(dir.Root, permissionSystemRoots : [Path.Combine(dir.Root, ".git")]);

        Assert.That(policy.Classify(target).Class, Is.EqualTo(ResourceClass.PermissionSystemProtected));
    }

    [Test]
    public void Classify_AdditionalReadRoot_And_WriteRoot_OutsideWorkspace()
    {
        using var workspaceDir = new TestTempDir();
        using var readDir      = new TestTempDir();
        using var writeDir     = new TestTempDir();
        var policy = new ResourceAccessPolicy(workspaceDir.Root,
                                              additionalReadRoots  : [readDir.Root],
                                              additionalWriteRoots : [writeDir.Root]);

        Assert.That(policy.Classify(Path.Combine(readDir.Root, "packages/x.dll")).Class,
                    Is.EqualTo(ResourceClass.AdditionalReadRoot));
        Assert.That(policy.Classify(Path.Combine(writeDir.Root, "tools/jb.exe")).Class,
                    Is.EqualTo(ResourceClass.AdditionalWriteRoot));
    }

    [Test]
    public void Classify_AdditionalWriteRoot_WinsOverReadRoot_OnOverlap()
    {
        using var workspaceDir = new TestTempDir();
        using var sharedDir    = new TestTempDir();
        var policy = new ResourceAccessPolicy(workspaceDir.Root,
                                              additionalReadRoots  : [sharedDir.Root],
                                              additionalWriteRoots : [sharedDir.Root]);

        Assert.That(policy.Classify(Path.Combine(sharedDir.Root, "x.txt")).Class,
                    Is.EqualTo(ResourceClass.AdditionalWriteRoot));
    }

    [Test]
    public void Classify_OutsideWorkspace_WhenNoRootMatches()
    {
        using var workspaceDir = new TestTempDir();
        using var outsideDir   = new TestTempDir();
        var policy = new ResourceAccessPolicy(workspaceDir.Root);

        Assert.That(policy.Classify(Path.Combine(outsideDir.Root, "notes.txt")).Class,
                    Is.EqualTo(ResourceClass.OutsideWorkspace));
    }

    [Test]
    public void Classify_WorkspaceSensitiveFile_StaysBaselineButMarksSensitive()
    {
        using var dir = new TestTempDir();
        var env = dir.WriteFile(".env", "SECRET=1");
        var policy = new ResourceAccessPolicy(dir.Root);

        // 工作区内部按基线处理，不自动识别秘密；敏感命中只如实标注。
        var classification = policy.Classify(env);
        Assert.That(classification.Class, Is.EqualTo(ResourceClass.BaselineWorkspace));
        Assert.That(classification.KnownSensitive, Is.True);
    }

    [Test]
    public void Classify_OutsideSensitiveHit_PromotesToSensitiveLocation()
    {
        using var workspaceDir = new TestTempDir();
        using var outsideDir   = new TestTempDir();
        var policy = new ResourceAccessPolicy(workspaceDir.Root);
        var target = Path.Combine(outsideDir.Root, ".ssh", "id_rsa");

        // 三级契约 C2 档：越界位置命中已知敏感名单即升为读保护敏感档。
        var classification = policy.Classify(target);
        Assert.Multiple(() =>
        {
            Assert.That(classification.Class, Is.EqualTo(ResourceClass.SensitiveLocation));
            Assert.That(classification.KnownSensitive, Is.True);
        });
    }

    [Test]
    public void Classify_OutsideSensitiveHit_ByFileNameAndExtension()
    {
        using var workspaceDir = new TestTempDir();
        using var outsideDir   = new TestTempDir();
        var policy = new ResourceAccessPolicy(workspaceDir.Root);

        var byFileName    = policy.Classify(Path.Combine(outsideDir.Root, "credentials.json"));
        var byExtension   = policy.Classify(Path.Combine(outsideDir.Root, "certs", "server.pem"));
        Assert.Multiple(() =>
        {
            Assert.That(byFileName.Class, Is.EqualTo(ResourceClass.SensitiveLocation));
            Assert.That(byExtension.Class, Is.EqualTo(ResourceClass.SensitiveLocation));
        });
    }

    [Test]
    public void Classify_OutsideNonSensitiveDotDirectory_StaysOutsideWorkspace()
    {
        using var workspaceDir = new TestTempDir();
        using var outsideDir   = new TestTempDir();
        var policy = new ResourceAccessPolicy(workspaceDir.Root);
        var target = Path.Combine(outsideDir.Root, ".cache", "x.bin");

        // 防过度归档负例：点目录本身不是敏感信号，普通越界仍为基线广泛读档。
        var classification = policy.Classify(target);
        Assert.Multiple(() =>
        {
            Assert.That(classification.Class, Is.EqualTo(ResourceClass.OutsideWorkspace));
            Assert.That(classification.KnownSensitive, Is.False);
        });
    }

    [Test]
    public void Classify_SandboxRuntimeRoot_Class()
    {
        using var workspaceDir  = new TestTempDir();
        using var runtimeDir    = new TestTempDir();
        var       runtimeFile   = runtimeDir.WriteFile("packages/x.dll", "bin");
        var policy = new ResourceAccessPolicy(workspaceDir.Root, sandboxRuntimeRoots : [runtimeDir.Root]);

        // 派生运行时写根：沙箱 OS 侧基线可写的基础设施，分类单列、注记不受敏感名单影响外溢。
        var classification = policy.Classify(runtimeFile);
        Assert.Multiple(() =>
        {
            Assert.That(classification.Class, Is.EqualTo(ResourceClass.SandboxRuntimeRoot));
            Assert.That(classification.KnownSensitive, Is.False);
        });
    }

    // ---------------------------------------------------------------- 链接改向

    [Test]
    public void Classify_JunctionIntoProtectedRoot_IsPermissionSystemProtected()
    {
        using var workspaceDir = new TestTempDir();
        using var configDir    = new TestTempDir();
        configDir.WriteFile("user-config.json", "{}");
        var link = Path.Combine(workspaceDir.Root, "cfglink");
        if (!TryCreateJunction(link, configDir.Root)) return;

        var policy = new ResourceAccessPolicy(workspaceDir.Root, permissionSystemRoots : [configDir.Root]);

        Assert.That(policy.Classify(Path.Combine(link, "user-config.json")).Class,
                    Is.EqualTo(ResourceClass.PermissionSystemProtected));
    }

    [Test]
    public void Classify_JunctionEscapingWorkspace_IsOutsideWorkspace()
    {
        using var workspaceDir = new TestTempDir();
        using var outsideDir   = new TestTempDir();
        outsideDir.WriteFile("notes.txt", "x");
        var link = Path.Combine(workspaceDir.Root, "outlink");
        if (!TryCreateJunction(link, outsideDir.Root)) return;

        var policy = new ResourceAccessPolicy(workspaceDir.Root);

        Assert.That(policy.Classify(Path.Combine(link, "notes.txt")).Class,
                    Is.EqualTo(ResourceClass.OutsideWorkspace));
    }

    [Test]
    public void EnsureNotPermissionProtected_ThrowsForJunctionIntoProtectedRoot()
    {
        using var workspaceDir = new TestTempDir();
        using var configDir    = new TestTempDir();
        configDir.WriteFile("user-config.json", "{}");
        var link = Path.Combine(workspaceDir.Root, "cfglink");
        if (!TryCreateJunction(link, configDir.Root)) return;

        var policy = new ResourceAccessPolicy(workspaceDir.Root, permissionSystemRoots : [configDir.Root]);

        var ex = TestAssert.Throws<InvalidDataException>(() =>
                   policy.EnsureNotPermissionProtected(Path.Combine(link, "user-config.json"), "File"));
        Assert.That(ex.Message, Does.Contain("permission-system protected"));
    }

    // ---------------------------------------------------------------- 分类器

    [Test]
    public void SensitivePathClassifier_MatchesKnownNamesAndExtensions()
    {
        Assert.That(SensitivePathClassifier.IsKnownSensitivePath(@"C:\Users\u\.ssh\id_rsa"), Is.True);
        Assert.That(SensitivePathClassifier.IsKnownSensitivePath(@"C:\w\.env"), Is.True);
        Assert.That(SensitivePathClassifier.IsKnownSensitivePath(@"C:\w\certs\server.pem"), Is.True);
        Assert.That(SensitivePathClassifier.IsKnownSensitivePath(@"C:\w\secrets.json"), Is.True);
        Assert.That(SensitivePathClassifier.IsKnownSensitivePath(@"C:\w\Program.cs"), Is.False);
        Assert.That(SensitivePathClassifier.IsKnownSensitivePath(""), Is.False);
    }

    // ---------------------------------------------------------------- Prepare 硬拒

    [Test]
    public void ReadFileTool_PrepareRejectsPermissionSystemProtectedPath()
    {
        using var dir = new TestTempDir();
        dir.CreateDirectory(".store");
        dir.WriteFile(".store/session.jsonl", "{}");
        var policy = new ResourceAccessPolicy(dir.Root, permissionSystemRoots : [Path.Combine(dir.Root, ".store")]);
        var tool   = new ReadFileTool(dir.Workspace, resourcePolicy : policy);

        var ex = TestAssert.Throws<InvalidDataException>(() =>
                   tool.Prepare(Call("read_file", new { path = ".store/session.jsonl" })));
        Assert.That(ex.Message, Does.Contain("permission system"));
    }

    [Test]
    public void ReadFileTool_PrepareRejectsExternalProtectedPath_WhenExternalPathsAllowed()
    {
        using var workspaceDir = new TestTempDir();
        using var configDir    = new TestTempDir();
        var config = configDir.WriteFile("user-config.json", "{}");
        // Desktop 组合允许准备外部路径；权限系统保护路径仍硬拒。
        var tool = new ReadFileTool(new Workspace(workspaceDir.Root, true),
                                    resourcePolicy : new ResourceAccessPolicy(workspaceDir.Root,
                                                              permissionSystemRoots : [configDir.Root]));

        TestAssert.Throws<InvalidDataException>(() =>
            tool.Prepare(Call("read_file", new { path = config })));
    }

    [Test]
    public void ReadFileTool_AllowsBaselineAndCarriesClassificationAnnotation()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("src.txt", "hello");
        var policy = new ResourceAccessPolicy(dir.Root);
        var tool   = new ReadFileTool(dir.Workspace, resourcePolicy : policy);

        var preparation = tool.Prepare(Call("read_file", new { path = "src.txt" }));

        Assert.That(preparation.ResourceClassification is { } c && c.Class == ResourceClass.BaselineWorkspace,
                    Is.True);
    }

    [Test]
    public void ListFilesTool_PrepareRejectsPermissionSystemProtectedPath()
    {
        using var dir = new TestTempDir();
        dir.CreateDirectory(".store");
        var policy = new ResourceAccessPolicy(dir.Root, permissionSystemRoots : [Path.Combine(dir.Root, ".store")]);
        var tool   = new ListFilesTool(dir.Workspace, resourcePolicy : policy);

        TestAssert.Throws<InvalidDataException>(() =>
            tool.Prepare(Call("list_files", new { path = ".store" })));
    }

    [Test]
    public void ApplyPatchTool_PrepareRejectsPatchTouchingProtectedPath()
    {
        using var dir = new TestTempDir();
        dir.CreateDirectory(".git");
        dir.WriteFile(".git/HEAD", "ref: refs/heads/main");
        var policy = new ResourceAccessPolicy(dir.Root, permissionSystemRoots : [Path.Combine(dir.Root, ".git")]);
        var tool   = new ApplyPatchTool(dir.Workspace, policy);
        var patch  = "--- a/.git/HEAD\n+++ b/.git/HEAD\n@@ -1,1 +1,2 @@\n ref: refs/heads/main\n+x\n";

        var ex = TestAssert.Throws<InvalidDataException>(() =>
                   tool.Prepare(Call("apply_patch", new { patch })));
        Assert.That(ex.Message, Does.Contain("permission system"));
    }

    [Test]
    public void ApplyPatchTool_AggregatesStrictestClassificationAcrossTargets()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("src.txt", "a");
        dir.CreateDirectory(".agents");
        dir.WriteFile(".agents/rules.md", "b");
        var tool  = new ApplyPatchTool(dir.Workspace, new ResourceAccessPolicy(dir.Root));
        var patch = "--- a/src.txt\n+++ b/src.txt\n@@ -1,1 +1,2 @@\n a\n+x\n" +
                    "--- a/.agents/rules.md\n+++ b/.agents/rules.md\n@@ -1,1 +1,2 @@\n b\n+x\n";

        var preparation = tool.Prepare(Call("apply_patch", new { patch }));

        Assert.That(preparation.ResourceClassification is { } c && c.Class == ResourceClass.ProtectedMetadata,
                    Is.True);
    }

    [Test]
    public void ApplyPatchTool_WithoutPolicy_KeepsLegacyBehavior()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("src.txt", "a");
        var tool  = new ApplyPatchTool(dir.Workspace);
        var patch = "--- a/src.txt\n+++ b/src.txt\n@@ -1,1 +1,2 @@\n a\n+x\n";

        var preparation = tool.Prepare(Call("apply_patch", new { patch }));

        Assert.That(preparation.ResourceClassification, Is.Null);
    }

    // ---------------------------------------------------------------- 注记传播

    [Test]
    public void GuardedTool_PropagatesClassificationAnnotation()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("src.txt", "hello");
        var state   = new WorkspaceAccessState(WorkspaceDirectoryIdentity.Capture(dir.Root));
        var guarded = state.Guard(() => new ReadFileTool(dir.Workspace,
                                                         resourcePolicy : new ResourceAccessPolicy(dir.Root)));

        var preparation = guarded.Prepare(Call("read_file", new { path = "src.txt" }));

        Assert.That(preparation.ResourceClassification is { } c && c.Class == ResourceClass.BaselineWorkspace,
                    Is.True);
        Assert.That(preparation.RequestedBackend, Is.Null);
    }

    [Test]
    public async Task WithInstructionCoverage_PreservesClassificationAndRequestedBackend()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("src.txt", "hello");
        var tool = new ReadFileTool(dir.Workspace, resourcePolicy : new ResourceAccessPolicy(dir.Root));
        var prepared = tool.Prepare(Call("read_file", new { path = "src.txt" }));
        var snapshot = await new ProjectInstructionLoader(dir.Workspace)
                          .LoadAsync(null, prepared.InstructionScopes, CancellationToken.None);

        var covered = prepared.WithInstructionCoverage(new InstructionCoverage(dir.Root, snapshot));

        Assert.That(covered.ResourceClassification, Is.EqualTo(prepared.ResourceClassification));
        Assert.That(covered.RequestedBackend, Is.EqualTo(prepared.RequestedBackend));
    }

    [Test]
    public void ToolPreparation_RequestedBackend_DefaultsToNullAndCarriesExplicitHost()
    {
        using var dir = new TestTempDir();
        var defaulted = new ToolPreparation
        {
            ToolName    = "shell",
            CallId      = "s1",
            Arguments   = new System.Text.Json.Nodes.JsonObject(),
            Capability  = "process.execute",
            Summary     = "shell",
            TargetPaths = [dir.Root]
        };
        Assert.That(defaulted.RequestedBackend, Is.Null);

        var explicitHost = new ToolPreparation
        {
            ToolName         = "shell",
            CallId           = "s2",
            Arguments        = new System.Text.Json.Nodes.JsonObject(),
            Capability       = "process.execute",
            Summary          = "shell",
            TargetPaths      = [dir.Root],
            RequestedBackend = ProcessExecutionBackendKind.Host
        };
        Assert.That(explicitHost.RequestedBackend, Is.EqualTo(ProcessExecutionBackendKind.Host));
    }

    // ---------------------------------------------------------------- 测试装置

    /// <summary>
    ///     以匿名对象序列化构造工具调用参数，避免手工转义 JSON 字符串。
    ///     Builds tool-call arguments by serializing an anonymous object, avoiding hand-escaped JSON.
    /// </summary>
    private static ChatToolCall Call(string functionName, object arguments) =>
        new(Guid.NewGuid().ToString("N"), functionName, JsonSerializer.Serialize(arguments));

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows()) return false;

        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
        {
            CreateNoWindow         = true,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };

        using var process = Process.Start(psi);
        if (process is null) return false;

        process.WaitForExit(5_000);
        return Directory.Exists(linkPath);
    }
}
