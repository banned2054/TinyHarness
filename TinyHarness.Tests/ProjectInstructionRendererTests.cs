using System.Security.Cryptography;
using System.Text;
using TinyHarness.Core.Models.ProjectInstructions;

namespace TinyHarness.Tests;

public class ProjectInstructionRendererTests
{
    [Test]
    public void DiscoveryAndLoadTimeDoNotChangeTheEffectiveRevision()
    {
        var document = Document("root rule");
        var first    = ProjectInstructionRenderer.Create([document], ["."]);
        var tracked =
            ProjectInstructionRenderer.Create([document with { LoadedUtc = DateTimeOffset.UtcNow.AddDays(1) }],
                                              ["src", "."]);
        Assert.That(first.Revision, Is.EqualTo(tracked.Revision));
        Assert.That(first.RenderedHash, Is.EqualTo(tracked.RenderedHash));
        Assert.That(first.DiscoveryFingerprint, Is.Not.EqualTo(tracked.DiscoveryFingerprint));
        Assert.That(tracked.ProbedDirectories, Is.EqualTo([".", "src"]));
    }

    [Test]
    public void BomIdentityAndEmptyRuleReplacementAreExplicit()
    {
        var plain = ProjectInstructionRenderer.Create([Document("rule")], ["."]);
        var bom   = ProjectInstructionRenderer.Create([Document("rule", true)], ["."]);
        Assert.That(plain.Revision, Is.Not.EqualTo(bom.Revision));
        Assert.That(bom.Documents[0].ByteCount, Is.EqualTo(7));
        var empty = ProjectInstructionRenderer.Create([], ["."]);
        Assert.That(ProjectInstructionRenderer.Render(empty),
                    Does.Contain("empty set invalidates all previous project rules"));
        Assert.DoesNotThrow(() => ProjectInstructionRenderer.Validate(empty));
    }

    [Test]
    public void RestoreValidationRejectsForgedHashesPathsAndIncompleteAncestorDiscovery()
    {
        var snapshot = ProjectInstructionRenderer.Create([Document("rule")], ["."]);
        Assert.Throws<InvalidDataException>(() => ProjectInstructionRenderer.Validate(snapshot with
        {
            Revision = "forged"
        }));
        Assert.Throws<InvalidDataException>(() => ProjectInstructionRenderer.Validate(snapshot with
        {
            RendererVersion = "unknown"
        }));
        Assert.Throws<InvalidDataException>(() => ProjectInstructionRenderer.Create([
            Document("rule") with
            {
                SourcePath = "../AGENTS.md"
            }
        ], ["."]));
        Assert.Throws<InvalidDataException>(() => ProjectInstructionRenderer.Create([], [".", "src/deep"]));
        Assert.Throws<InvalidDataException>(() => ProjectInstructionRenderer.Create([
            Document("rule") with
            {
                ContentHash = new string('0', 64)
            }
        ], ["."]));
    }

    /// <summary>
    ///     <para>从合成 UTF-8 字节创建原始身份一致的根规则。</para>
    ///     <para>Creates a root rule with a consistent raw identity from synthetic UTF-8 bytes.</para>
    /// </summary>
    private static ProjectInstructionDocument Document(string content, bool bom = false)
    {
        var bytes      = Encoding.UTF8.GetBytes(content);
        if (bom) bytes = [0xEF, 0xBB, 0xBF, .. bytes];
        return new ProjectInstructionDocument
        {
            SourcePath     = "AGENTS.md",
            ScopeDirectory = ".",
            Content        = content,
            ContentHash    = Convert.ToHexString(SHA256.HashData(bytes)),
            ByteCount      = bytes.Length,
            HasUtf8Bom     = bom,
            LoadedUtc      = DateTimeOffset.UtcNow
        };
    }
}
