using System.Security.Cryptography;
using System.Text;
using TinyHarness.Core.Models.ProjectInstructions;
using TinyHarness.Core.Services.ProjectInstructions;

namespace TinyHarness.Tests;

public class ProjectInstructionLoaderTests
{
    [TestCase("documents", "64-document")]
    [TestCase("bytes", "128-KiB")]
    [TestCase("tokens", "8,000-token")]
    [TestCase("probes", "2,048")]
    public async Task FixedBudgetsRejectTheWholeCandidate(string budget, string expectedReason)
    {
        using var dir    = new TestTempDir();
        var       scopes = new List<InstructionScope>();
        var       count  = budget switch { "documents" => 65, "bytes" => 5, "probes" => 2_048, _ => 0 };
        for (var index = 0; index < count; index++)
        {
            var directory = "scope" + index;
            scopes.Add(new InstructionScope(directory, InstructionScopeKind.Directory));
            if (budget != "probes")
                dir.WriteFile(directory + "/AGENTS.md", budget == "bytes" ? new string('x', 27_000) : "rule");
        }

        if (budget == "tokens") dir.WriteFile("AGENTS.md", new string('x', 32_000));
        var error = await TestAssert.ThrowsAsync<InvalidDataException>(() =>
                                                                           new ProjectInstructionLoader(dir.Workspace)
                                                                              .LoadAsync(null, scopes,
                                                                                   CancellationToken.None));
        Assert.That(error.Message, Does.Contain(expectedReason));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RootReadPreservesUtf8BytesAndWhitespace(bool bom)
    {
        using var    dir     = new TestTempDir();
        const string content = "  规则\n\n";
        var          bytes   = Encoding.UTF8.GetBytes(content);
        if (bom) bytes       = [0xEF, 0xBB, 0xBF, .. bytes];
        dir.WriteBytes("AGENTS.md", bytes);
        var snapshot = await new ProjectInstructionLoader(dir.Workspace).LoadAsync(null, [], CancellationToken.None);
        var document = snapshot.Documents.Single();
        Assert.That(document.Content, Is.EqualTo(content));
        Assert.That(document.HasUtf8Bom, Is.EqualTo(bom));
        Assert.That(document.ByteCount, Is.EqualTo(bytes.Length));
        Assert.That(document.ContentHash, Is.EqualTo(Convert.ToHexString(SHA256.HashData(bytes))));
    }

    [Test]
    public async Task FileScopesMergeAncestorChainsAndKeepSiblingScopesSeparate()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("AGENTS.md", "root");
        dir.WriteFile("src/AGENTS.md", "src");
        dir.WriteFile("src/a/AGENTS.md", "a");
        dir.WriteFile("src/b/AGENTS.md", "b");
        var snapshot = await new ProjectInstructionLoader(dir.Workspace).LoadAsync(null,
            [
                new InstructionScope("src/b/new/file.cs", InstructionScopeKind.File),
                new InstructionScope("src/a/file.cs", InstructionScopeKind.File)
            ],
            CancellationToken.None);
        Assert.That(snapshot.Documents.Select(document => document.ScopeDirectory),
                    Is.EqualTo([".", "src", "src/a", "src/b"]));
        Assert.That(snapshot.ProbedDirectories, Does.Contain("src/b/new"));
        Assert.That(ProjectInstructionRenderer.Render(snapshot),
                    Does.Contain("Scope: src/a").And.Contain("Scope: src/b"));
    }

    [Test]
    public async Task MissingPositionsAreTrackedAndLaterCreationAndRemovalRefreshTheWholeSet()
    {
        using var dir    = new TestTempDir();
        var       loader = new ProjectInstructionLoader(dir.Workspace);
        var       root   = await loader.LoadAsync(null, [], CancellationToken.None);
        var probed = await loader.LoadAsync(root, [new InstructionScope("new/file.cs", InstructionScopeKind.File)],
                                            CancellationToken.None);
        Assert.That(probed.Revision, Is.EqualTo(root.Revision));
        Assert.That(probed.DiscoveryFingerprint, Is.Not.EqualTo(root.DiscoveryFingerprint));
        dir.WriteFile("new/AGENTS.md", "new rule");
        var created = await loader.LoadAsync(probed, [], CancellationToken.None);
        Assert.That(created.Documents.Single().ScopeDirectory, Is.EqualTo("new"));
        File.Delete(Path.Combine(dir.Root, "new", "AGENTS.md"));
        var removed = await loader.LoadAsync(created, [], CancellationToken.None);
        Assert.That(removed.Documents, Is.Empty);
        Assert.That(removed.Revision, Is.EqualTo(root.Revision));
        Assert.That(removed.ProbedDirectories, Does.Contain("new"));
    }

    [Test]
    public async Task RecursiveDiscoveryUsesTheToolWalkerDepthAndExclusions()
    {
        using var dir = new TestTempDir();
        dir.WriteFile("src/AGENTS.md", "src rule");
        dir.WriteFile("src/deep/AGENTS.md", "deep rule");
        dir.WriteFile("bin/AGENTS.md", "excluded");
        var loader = new ProjectInstructionLoader(dir.Workspace);
        var shallow =
            await loader.LoadAsync(null, [new InstructionScope(".", InstructionScopeKind.RecursiveDirectory, 1)],
                                   CancellationToken.None);
        Assert.That(shallow.Documents.Select(document => document.ScopeDirectory), Is.EqualTo(["src"]));
        var deep = await loader.LoadAsync(null, [new InstructionScope(".", InstructionScopeKind.RecursiveDirectory)],
                                          CancellationToken.None);
        Assert.That(deep.Documents.Select(document => document.ScopeDirectory),
                    Is.EqualTo(["src", "src/deep"]));
        Assert.That(deep.ProbedDirectories, Does.Not.Contain("bin"));
    }

    [Test]
    public async Task EmptyFilesAndMissingRootAreValidEmptyRuleSets()
    {
        using var dir     = new TestTempDir();
        var       loader  = new ProjectInstructionLoader(dir.Workspace);
        var       missing = await loader.LoadAsync(null, [], CancellationToken.None);
        dir.WriteBytes("AGENTS.md", [0xEF, 0xBB, 0xBF]);
        var empty = await loader.LoadAsync(missing, [], CancellationToken.None);
        Assert.That(empty.Documents, Is.Empty);
        Assert.That(empty.Revision, Is.EqualTo(missing.Revision));
    }

    [Test]
    public async Task InvalidEncodingOversizeAndLockedSourcesFailClosed()
    {
        using var dir    = new TestTempDir();
        var       loader = new ProjectInstructionLoader(dir.Workspace);
        var       path   = dir.WriteBytes("AGENTS.md", [0xC3, 0x28]);
        await TestAssert.ThrowsAsync<InvalidDataException>(async () =>
                                                               await loader
                                                                  .LoadAsync(null, [], CancellationToken.None));
        dir.WriteBytes("AGENTS.md", new byte[ProjectInstructionLimits.MaxDocumentBytes + 1]);
        var over =
            await TestAssert.ThrowsAsync<InvalidDataException>(async () =>
                                                                   await loader.LoadAsync(null, [],
                                                                       CancellationToken.None));
        Assert.That(over!.Message, Does.Contain("32 KiB"));
        dir.WriteFile("AGENTS.md", "rule");
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await TestAssert.ThrowsAsync<IOException>(async () => await loader.LoadAsync(null, [], CancellationToken.None));
    }

    [Test]
    public async Task CancellationAndLexicalEscapesAreRejectedBeforeReading()
    {
        using var dir    = new TestTempDir();
        var       loader = new ProjectInstructionLoader(dir.Workspace);
        await TestAssert.ThrowsAsync<OperationCanceledException>(async () =>
                                                                     await loader.LoadAsync(null, [],
                                                                         new CancellationToken(true)));
        await TestAssert.ThrowsAsync<InvalidDataException>(async () => await loader.LoadAsync(null,
        [
            new InstructionScope("../outside/file.cs", InstructionScopeKind.File)
        ], CancellationToken.None));
    }

    [Test]
    public async Task RuleLinksAndDirectoryJunctionsAreRejected()
    {
        using var dir     = new TestTempDir();
        using var outside = new TestTempDir();
        outside.WriteFile("AGENTS.md", "outside rule");
        try
        {
            File.CreateSymbolicLink(Path.Combine(dir.Root, "AGENTS.md"), Path.Combine(outside.Root, "AGENTS.md"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Ignore("Symbolic-link creation is unavailable: " + ex.GetType().Name);
        }

        var loader = new ProjectInstructionLoader(dir.Workspace);
        await TestAssert.ThrowsAsync<InvalidDataException>(async () =>
                                                               await loader
                                                                  .LoadAsync(null, [], CancellationToken.None));
        File.Delete(Path.Combine(dir.Root, "AGENTS.md"));
        Directory.CreateSymbolicLink(Path.Combine(dir.Root, "linked"), outside.Root);
        await TestAssert.ThrowsAsync<InvalidDataException>(async () => await loader.LoadAsync(null,
        [
            new InstructionScope("linked/file.cs", InstructionScopeKind.File)
        ], CancellationToken.None));
        await Task.CompletedTask;
    }
}
