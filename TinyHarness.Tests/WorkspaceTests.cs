using System.Diagnostics;

namespace TinyHarness.Tests;

public class WorkspaceTests
{
    [Test]
    public void ResolveInside_RejectsParentTraversal()
    {
        using var dir       = new TestTempDir();
        var       workspace = dir.Workspace;

        var ex = TestAssert.Throws<InvalidDataException>(() => workspace.ResolveInside("../outside.txt", "path"));
        Assert.That(ex.Message, Does.Contain("escapes the workspace root"));
    }

    [Test]
    public void ResolveInside_RejectsAbsolutePathOutsideRoot()
    {
        using var inside    = new TestTempDir();
        using var outside   = new TestTempDir();
        var       workspace = inside.Workspace;

        var target = Path.Combine(outside.Root, "secret.txt");
        TestAssert.Throws<InvalidDataException>(() => workspace.ResolveInside(target, "path"));
    }

    [Test]
    public void ResolveInside_AllowsNestedPathInsideRoot()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("src/Program.cs", "x");
        var workspace = dir.Workspace;

        var resolved = workspace.ResolveInside("src/Program.cs", "path");

        Assert.That(resolved, Is.EqualTo(Path.Combine(dir.Root, "src", "Program.cs")));
    }

    [Test]
    public void ResolveInside_AllowsAbsolutePathInsideRoot()
    {
        using var dir       = new TestTempDir();
        var       file      = dir.WriteFile("a.txt", "x");
        var       workspace = dir.Workspace;

        Assert.That(workspace.ResolveInside(file, "path"), Is.EqualTo(file));
    }

    [Test]
    public void EnsureFinalTargetInside_RejectsLexicallyOutsidePath()
    {
        using var insideDir  = new TestTempDir();
        using var outsideDir = new TestTempDir();
        var       outside    = outsideDir.WriteFile("outside.txt", "outside");

        var ex =
            TestAssert.Throws<InvalidDataException>(() =>
                                                        insideDir.Workspace.EnsureFinalTargetInside(outside,
                                                                 false, "File"));

        Assert.That(ex.Message, Does.Contain("is outside the workspace root"));
    }

    [Test]
    public void ToDisplay_UsesForwardSlashesAndDotForRoot()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("src/deep/File.cs", "x");
        var workspace = dir.Workspace;

        Assert.That(workspace.ToDisplay(dir.Root), Is.EqualTo("."));
        Assert.That(workspace.ToDisplay(Path.Combine(dir.Root, "src", "deep", "File.cs")),
                    Is.EqualTo("src/deep/File.cs"));
    }

    [Test]
    public void ReadFile_ThroughJunctionEscapingRoot_IsRejected()
    {
        using var workspaceDir = new TestTempDir();
        using var outsideDir   = new TestTempDir();
        outsideDir.WriteFile("secret.txt", "do not leak");
        var workspace = workspaceDir.Workspace;

        var junction = Path.Combine(workspaceDir.Root, "link");
        if (!TryCreateJunction(junction, outsideDir.Root))
            return; // 环境不允许创建 junction；无可验证内容。

        // 词法解析没问题：路径在纸面上位于根内。
        var pathInside = workspace.ResolveInside("link/secret.txt", "path");

        var ex =
            TestAssert.Throws<InvalidDataException>(() => workspace.EnsureFinalTargetInside(pathInside, false,
                                                             "File"));
        Assert.That(ex.Message, Does.Contain("resolves outside the workspace root"));
    }

    [Test]
    public void ReadFile_ThroughFileSymlinkEscapingRoot_IsRejected()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var workspaceDir = new TestTempDir();
        using var outsideDir   = new TestTempDir();
        var       outside      = outsideDir.WriteFile("secret.txt", "do not leak");
        var       workspace    = workspaceDir.Workspace;

        var linkPath = Path.Combine(workspaceDir.Root, "link.txt");
        try
        {
            File.CreateSymbolicLink(linkPath, outside);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return; // 无符号链接权限（开发者模式）；无可验证内容。
        }

        var pathInside = workspace.ResolveInside("link.txt", "path");
        var boundaryEx =
            TestAssert.Throws<InvalidDataException>(() => workspace.EnsureFinalTargetInside(pathInside, false,
                                                             "File"));
        Assert.That(boundaryEx.Message, Does.Contain("resolves outside the workspace root"));
    }

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
        return process.ExitCode == 0;
    }
}
