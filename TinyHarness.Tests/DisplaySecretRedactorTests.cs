using TinyHarness.Cli.Services;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         展示脱敏契约：整段替换、跨增量片段替换、尾部保留与冲刷全量扫描（短密钥不得在
///         未扫描尾部泄漏）。
///     </para>
///     <para>
///         Display-redaction contract: whole-text replacement, replacement across delta
///         fragments, tail hold-back, and a flush that scans everything (a short secret must
///         not leak through the unscanned tail).
///     </para>
/// </summary>
public class DisplaySecretRedactorTests
{
    private static IReadOnlyDictionary<string, string> Secrets(params string[] values)
    {
        return values.ToDictionary(value => $"name-{value}", value => value, StringComparer.Ordinal);
    }

    [Test]
    public void Push_ReplacesSecretSplitAcrossDeltas()
    {
        var redactor = new DisplaySecretRedactor(Secrets("sk-abc123xyz"));

        var display = redactor.Push("key is sk-ab") + redactor.Push("c123xyz done") + redactor.Flush();

        Assert.That(display, Is.EqualTo("key is [REDACTED] done"));
    }

    [Test]
    public void Push_HoldsBackPartialPrefixUntilComplete()
    {
        var redactor = new DisplaySecretRedactor(Secrets("SECRET"));

        // 尾部扣留偏保守（最长密钥长度减一），会多扣一点安全文本；密钥补全后整体被脱敏。
        var first = redactor.Push("The key is SEC");
        var rest  = redactor.Push("RET here");

        Assert.That(first, Is.EqualTo("The key i"));
        Assert.That(first + rest + redactor.Flush(), Is.EqualTo("The key is [REDACTED] here"));
    }

    [Test]
    public void Flush_ScansEverything_ShortSecretInsideTailDoesNotLeak()
    {
        // "ab" 是比最长密钥更短的完整密钥；流结束时若未再扫描尾部就原样冲刷，会把它泄漏出去。
        var redactor = new DisplaySecretRedactor(Secrets("ab", "abcdefg"));

        var display = redactor.Push("abc") + redactor.Flush();

        Assert.That(display, Is.EqualTo("[REDACTED]c"));
    }

    [Test]
    public void FlushSafe_AtStreamBoundary_HoldsSecretPrefixForTheNextStream()
    {
        // 两段增量分属两次模型流且中间隔着一个行事件；边界冲刷必须扣留 "sk-secret-" 前缀而非
        // 直接显示，下一段流的补全才能脱敏整个密钥。
        var redactor = new DisplaySecretRedactor(Secrets("sk-secret-value"));

        var display = redactor.Push("see sk-secret-")
                    + redactor.FlushSafe()
                    + redactor.Push("value now")
                    + redactor.Flush();

        Assert.That(display, Is.EqualTo("see [REDACTED] now"));
    }

    [Test]
    public void FlushSafe_HoldsShortSecretInsideAPrefixTail_UntilItIsFullyScanned()
    {
        // "abc" 是 "abcdefg" 的严格前缀且包含完整密钥 "ab"；扣留它只是推迟替换，而不是
        // 原样泄漏 "ab"。
        var redactor = new DisplaySecretRedactor(Secrets("ab", "abcdefg"));

        var display = redactor.Push("xxabc") + redactor.FlushSafe();

        Assert.That(display, Is.EqualTo("xx"));
        Assert.That(display + redactor.Push("defgh") + redactor.Flush(), Is.EqualTo("xx[REDACTED]h"));
    }

    [Test]
    public void Redact_ReplacesTheLongestSecretWhenShorterOnesOverlap()
    {
        var redactor = new DisplaySecretRedactor(Secrets("ab", "abcdefg"));

        // "abcd" 不含 "abcdefg"，只有 "ab" 匹配；更长密钥只要出现就优先，"ab" 不会被
        // 先部分替换。
        Assert.That(redactor.Redact("abcd"), Is.EqualTo("[REDACTED]cd"));
        Assert.That(redactor.Redact("xabcdefgz"), Is.EqualTo("x[REDACTED]z"));
    }

    [Test]
    public void Push_MatchesTheLongestSecretAtEachIndex()
    {
        var redactor = new DisplaySecretRedactor(Secrets("ab", "abcdefg"));

        Assert.That(redactor.Push("abcdefg") + redactor.Flush(), Is.EqualTo("[REDACTED]"));
    }

    [Test]
    public void Redact_ReplacesEveryOccurrenceInWholeText()
    {
        var redactor = new DisplaySecretRedactor(Secrets("tok123"));

        Assert.That(redactor.Redact("a=tok123 b=tok123"), Is.EqualTo("a=[REDACTED] b=[REDACTED]"));
    }

    [Test]
    public void WithoutSecrets_EveryPathPassesThrough()
    {
        var redactor = new DisplaySecretRedactor(null);

        Assert.That(redactor.HasSecrets, Is.False);
        Assert.That(redactor.Redact("raw text"), Is.EqualTo("raw text"));
        Assert.That(redactor.Push("raw"), Is.EqualTo("raw"));
        Assert.That(redactor.Flush(), Is.EqualTo(string.Empty));
    }

    [Test]
    public void Push_EmitsNothingWhileBufferIsShorterThanTheLongestSecret()
    {
        var redactor = new DisplaySecretRedactor(Secrets("longsecret"));

        Assert.That(redactor.Push("abc"), Is.EqualTo(string.Empty));
        Assert.That(redactor.Flush(), Is.EqualTo("abc"));
    }
}
