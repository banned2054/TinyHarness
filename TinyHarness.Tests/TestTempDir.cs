using TinyHarness.Core.Runtime;

namespace TinyHarness.Tests;

/// <summary>
/// A disposable unique temp directory used as a fake workspace root in M3 tests.
/// </summary>
internal sealed class TestTempDir : IDisposable
{
    public TestTempDir()
    {
        Root = Path.Combine(Path.GetTempPath(), "tinyharness-m3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public Workspace Workspace => new(Root);

    public string WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public string WriteBytes(string relativePath, byte[] content)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return full;
    }

    public string CreateDirectory(string relativePath)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive : true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of a temp directory.
        }
    }
}
