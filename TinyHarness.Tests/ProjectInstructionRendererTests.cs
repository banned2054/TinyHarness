using System.Security.Cryptography;
using System.Text;
using TinyHarness.Core.Models.ProjectInstructions;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Services.Context;

namespace TinyHarness.Tests;

public class ProjectInstructionRendererTests
{
    [TestCase("ascii")]
    [TestCase("cjk")]
    [TestCase("emoji")]
    public void RenderBudgetsIncludeFramingAndNoticesWithoutBreakingUnicode(string alphabet)
    {
        var content = alphabet switch
        {
            "cjk" => new string('规', 40_000),
            "emoji" => string.Concat(Enumerable.Repeat("😀", 40_000)),
            _ => new string('x', 100_000)
        };
        var snapshot = ProjectInstructionRenderer.Create([Document(content)], ["."]);
        var rendered = ProjectInstructionRenderer.Render(snapshot);
        Assert.That(new UTF8Encoding(false, true).GetByteCount(rendered),
                    Is.LessThanOrEqualTo(ProjectInstructionLimits.MaxRenderedBytes));
        Assert.That(TokenEstimator.EstimateMessage(ChatMessage.User(rendered)),
                    Is.LessThanOrEqualTo(ProjectInstructionLimits.MaxTokens));
        Assert.That(rendered, Does.Contain("truncated AGENTS.md").And.Contain("</project-instruction>"));
        Assert.That(snapshot.Documents.Single().Content, Is.EqualTo(content));
        Assert.DoesNotThrow(() => ProjectInstructionRenderer.Validate(snapshot));
    }

    [Test]
    public void RenderBudgetOmitsBroaderRulesBeforeTruncatingSpecificRules()
    {
        var root = Document(new string('r', 100_000));
        var local = Document("SPECIFIC_RULE") with { SourcePath = "src/AGENTS.md", ScopeDirectory = "src" };
        var snapshot = ProjectInstructionRenderer.Create([root, local], [".", "src"]);
        var rendered = ProjectInstructionRenderer.Render(snapshot);
        Assert.That(rendered, Does.Contain("omitted AGENTS.md").And.Contain("SPECIFIC_RULE"));
        Assert.That(rendered, Does.Not.Contain("truncated src/AGENTS.md"));
        Assert.That(snapshot.Documents.Count, Is.EqualTo(2));
    }

    [Test]
    public void DiscoveryAndLoadTimeDoNotChangeTheEffectiveRevision()
    {
        var document = Document("root rule");
        var first = ProjectInstructionRenderer.Create([document], ["."]);
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
        var bom = ProjectInstructionRenderer.Create([Document("rule", true)], ["."]);
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
        var bytes = Encoding.UTF8.GetBytes(content);
        if (bom) bytes = [0xEF, 0xBB, 0xBF, .. bytes];
        return new ProjectInstructionDocument
        {
            SourcePath = "AGENTS.md",
            ScopeDirectory = ".",
            Content = content,
            ContentHash = Convert.ToHexString(SHA256.HashData(bytes)),
            ByteCount = bytes.Length,
            HasUtf8Bom = bom,
            LoadedUtc = DateTimeOffset.UtcNow
        };
    }
}
