using System.Diagnostics;

namespace TinyHarness.Tests;

public class WorkspaceTests
{
    [Fact]
    public void ResolveInside_RejectsParentTraversal()
    {
        using var dir       = new TestTempDir();
        var       workspace = dir.Workspace;

        var ex = Assert.Throws<InvalidDataException>(() => workspace.ResolveInside("../outside.txt", "path"));
        Assert.Contains("escapes the workspace root", ex.Message);
    }

    [Fact]
    public void ResolveInside_RejectsAbsolutePathOutsideRoot()
    {
        using var inside    = new TestTempDir();
        using var outside   = new TestTempDir();
        var       workspace = inside.Workspace;

        var target = Path.Combine(outside.Root, "secret.txt");
        Assert.Throws<InvalidDataException>(() => workspace.ResolveInside(target, "path"));
    }

    [Fact]
    public void ResolveInside_AllowsNestedPathInsideRoot()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("src/Program.cs", "x");
        var workspace = dir.Workspace;

        var resolved = workspace.ResolveInside("src/Program.cs", "path");

        Assert.Equal(Path.Combine(dir.Root, "src", "Program.cs"), resolved);
    }

    [Fact]
    public void ResolveInside_AllowsAbsolutePathInsideRoot()
    {
        using var dir       = new TestTempDir();
        var       file      = dir.WriteFile("a.txt", "x");
        var       workspace = dir.Workspace;

        Assert.Equal(file, workspace.ResolveInside(file, "path"));
    }

    [Fact]
    public void ToDisplay_UsesForwardSlashesAndDotForRoot()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("src/deep/File.cs", "x");
        var workspace = dir.Workspace;

        Assert.Equal(".", workspace.ToDisplay(dir.Root));
        Assert.Equal("src/deep/File.cs", workspace.ToDisplay(Path.Combine(dir.Root, "src", "deep", "File.cs")));
    }

    [Fact]
    public void ReadFile_ThroughJunctionEscapingRoot_IsRejected()
    {
        using var workspaceDir = new TestTempDir();
        using var outsideDir   = new TestTempDir();
        outsideDir.WriteFile("secret.txt", "do not leak");
        var workspace = workspaceDir.Workspace;

        var junction = Path.Combine(workspaceDir.Root, "link");
        if (!TryCreateJunction(junction, outsideDir.Root))
        {
            return; // Environment does not permit junction creation; nothing to verify.
        }

        // Lexical resolution is fine: the path is inside the root on paper.
        var pathInside = workspace.ResolveInside("link/secret.txt", "path");

        var ex =
            Assert.Throws<InvalidDataException>(() => workspace.EnsureFinalTargetInside(pathInside, isDirectory : false,
                                                         "File"));
        Assert.Contains("resolves outside the workspace root", ex.Message);
    }

    [Fact]
    public void ReadFile_ThroughFileSymlinkEscapingRoot_IsRejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

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
            return; // No symlink privilege (Developer Mode); nothing to verify.
        }

        var pathInside = workspace.ResolveInside("link.txt", "path");
        var boundaryEx =
            Assert.Throws<InvalidDataException>(() => workspace.EnsureFinalTargetInside(pathInside, isDirectory : false,
                                                         "File"));
        Assert.Contains("resolves outside the workspace root", boundaryEx.Message);
    }

    private static bool TryCreateJunction(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var psi = new ProcessStartInfo("cmd.exe",
                                       $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
        {
            CreateNoWindow         = true,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            return false;
        }

        process.WaitForExit(5_000);
        return process.ExitCode == 0;
    }
}
