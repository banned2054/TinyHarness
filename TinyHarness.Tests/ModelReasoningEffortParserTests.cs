using TinyHarness.Core.Models.ChatCompletions;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         档位解析与规范写出：全集大小写不敏感、兼容同义词（"off"/"extra-high"，读取旧配置与会话）与
///         写出端互逆；"ultra" 不是本目录的档位，解析必须拒绝。
///     </para>
///     <para>
///         Effort parsing and canonical writing: the case-insensitive full set, compatibility
///         synonyms ("off"/"extra-high" for reading old configs and sessions), the write-side
///         inverse, and rejection of "ultra", which is not a level of this catalog.
///     </para>
/// </summary>
public class ModelReasoningEffortParserTests
{
    [TestCase("none", ModelReasoningEffort.None)]
    [TestCase("minimal", ModelReasoningEffort.Minimal)]
    [TestCase("low", ModelReasoningEffort.Low)]
    [TestCase("medium", ModelReasoningEffort.Medium)]
    [TestCase("high", ModelReasoningEffort.High)]
    [TestCase("xhigh", ModelReasoningEffort.ExtraHigh)]
    [TestCase("max", ModelReasoningEffort.Max)]
    [TestCase("off", ModelReasoningEffort.None)]
    [TestCase("extra-high", ModelReasoningEffort.ExtraHigh)]
    [TestCase(" XHIGH ", ModelReasoningEffort.ExtraHigh)]
    [TestCase("Max", ModelReasoningEffort.Max)]
    public void ParsesCanonicalAndCompatibilityValues(string text, ModelReasoningEffort expected)
    {
        Assert.That(ModelReasoningEffortParser.TryParse(text, out var value), Is.True);
        Assert.That(value, Is.EqualTo(expected));
    }

    [TestCase("turbo")]
    [TestCase("ultra")]
    [TestCase("")]
    [TestCase(null)]
    public void RejectsUnknownValues(string? text)
    {
        Assert.That(ModelReasoningEffortParser.TryParse(text, out _), Is.False);
    }

    [Test]
    public void ToValueString_RoundTripsThroughParser()
    {
        foreach (var level in Enum.GetValues<ModelReasoningEffort>())
        {
            var written = ModelReasoningEffortParser.ToValueString(level);
            Assert.That(ModelReasoningEffortParser.TryParse(written, out var parsed), Is.True);
            Assert.That(parsed, Is.EqualTo(level));
        }
    }
}
