using TinyHarness.Cli.Services;

namespace TinyHarness.Tests;

/// <summary>
///     展示脱敏契约：整段替换、跨增量片段替换、尾部保留与冲刷全量扫描（短密钥不得在
///     未扫描尾部泄漏）。
///     Display-redaction contract: whole-text replacement, replacement across delta
///     fragments, tail hold-back, and a flush that scans everything (a short secret must
///     not leak through the unscanned tail).
/// </summary>
public class DisplaySecretRedactorTests
{
    private static IReadOnlyDictionary<string, string> Secrets(params string[] values)
    {
        return values.ToDictionary(value => $"name-{value}", value => value, StringComparer.Ordinal);
    }

    [Fact]
    public void Push_ReplacesSecretSplitAcrossDeltas()
    {
        var redactor = new DisplaySecretRedactor(Secrets("sk-abc123xyz"));

        var display = redactor.Push("key is sk-ab") + redactor.Push("c123xyz done") + redactor.Flush();

        Assert.Equal("key is [REDACTED] done", display);
    }

    [Fact]
    public void Push_HoldsBackPartialPrefixUntilComplete()
    {
        var redactor = new DisplaySecretRedactor(Secrets("SECRET"));

        // The tail hold-back is conservative (longest secret minus one), so a little
        // safe text trails behind; the secret itself is redacted once complete.
        var first = redactor.Push("The key is SEC");
        var rest  = redactor.Push("RET here");

        Assert.Equal("The key i", first);
        Assert.Equal("The key is [REDACTED] here", first + rest + redactor.Flush());
    }

    [Fact]
    public void Flush_ScansEverything_ShortSecretInsideTailDoesNotLeak()
    {
        // "ab" is a full secret shorter than the longest one; a raw-tail flush would
        // leak it once the stream ends before the tail is scanned again.
        var redactor = new DisplaySecretRedactor(Secrets("ab", "abcdefg"));

        var display = redactor.Push("abc") + redactor.Flush();

        Assert.Equal("[REDACTED]c", display);
    }

    [Fact]
    public void FlushSafe_AtStreamBoundary_HoldsSecretPrefixForTheNextStream()
    {
        // The two deltas belong to two separate model streams with a line event in
        // between; the boundary flush must hold the "sk-secret-" prefix instead of
        // displaying it, so the completion in the next stream redacts the whole secret.
        var redactor = new DisplaySecretRedactor(Secrets("sk-secret-value"));

        var display = redactor.Push("see sk-secret-")
                    + redactor.FlushSafe()
                    + redactor.Push("value now")
                    + redactor.Flush();

        Assert.Equal("see [REDACTED] now", display);
    }

    [Fact]
    public void FlushSafe_HoldsShortSecretInsideAPrefixTail_UntilItIsFullyScanned()
    {
        // "abc" is a strict prefix of "abcdefg" and contains the complete secret "ab";
        // holding it back defers the replacement instead of leaking "ab" raw.
        var redactor = new DisplaySecretRedactor(Secrets("ab", "abcdefg"));

        var display = redactor.Push("xxabc") + redactor.FlushSafe();

        Assert.Equal("xx", display);
        Assert.Equal("xx[REDACTED]h", display + redactor.Push("defgh") + redactor.Flush());
    }

    [Fact]
    public void Redact_ReplacesTheLongestSecretWhenShorterOnesOverlap()
    {
        var redactor = new DisplaySecretRedactor(Secrets("ab", "abcdefg"));

        // "abcd" holds no "abcdefg" occurrence, so only "ab" matches; the longer secret
        // wins whenever it is present, so "ab" is never partially replaced first.
        Assert.Equal("[REDACTED]cd", redactor.Redact("abcd"));
        Assert.Equal("x[REDACTED]z", redactor.Redact("xabcdefgz"));
    }

    [Fact]
    public void Push_MatchesTheLongestSecretAtEachIndex()
    {
        var redactor = new DisplaySecretRedactor(Secrets("ab", "abcdefg"));

        Assert.Equal("[REDACTED]", redactor.Push("abcdefg") + redactor.Flush());
    }

    [Fact]
    public void Redact_ReplacesEveryOccurrenceInWholeText()
    {
        var redactor = new DisplaySecretRedactor(Secrets("tok123"));

        Assert.Equal("a=[REDACTED] b=[REDACTED]", redactor.Redact("a=tok123 b=tok123"));
    }

    [Fact]
    public void WithoutSecrets_EveryPathPassesThrough()
    {
        var redactor = new DisplaySecretRedactor(null);

        Assert.False(redactor.HasSecrets);
        Assert.Equal("raw text", redactor.Redact("raw text"));
        Assert.Equal("raw", redactor.Push("raw"));
        Assert.Equal(string.Empty, redactor.Flush());
    }

    [Fact]
    public void Push_EmitsNothingWhileBufferIsShorterThanTheLongestSecret()
    {
        var redactor = new DisplaySecretRedactor(Secrets("longsecret"));

        Assert.Equal(string.Empty, redactor.Push("abc"));
        Assert.Equal("abc", redactor.Flush());
    }
}
