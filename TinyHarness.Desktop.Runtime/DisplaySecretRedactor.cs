using System.Text;

namespace TinyHarness.Desktop.Runtime;

/// <summary>
///     <para>诊断和显式脱敏出口使用的已知密钥替换器，普通本地正文不默认经过此类。</para>
///     <para>Replaces known keys for diagnostics and explicitly redacted output; ordinary local content bypasses it.</para>
/// </summary>
/// <remarks>
///     <para>支持整段和跨片段替换；中途冲刷保留可能的密钥前缀，终态冲刷扫描全部剩余内容。</para>
///     <para>Supports whole-text and fragmented replacement; intermediate flushes retain possible key prefixes and final flushes scan all remaining text.</para>
/// </remarks>
internal sealed class DisplaySecretRedactor
{
    private const    string                               Replacement = "[REDACTED]";
    private int _maximumSecretLength;

    private          IReadOnlyList<string> _secrets = [];
    private readonly IReadOnlyDictionary<string, string>? _secretSource;

    private string _pending = string.Empty;

    public DisplaySecretRedactor(IReadOnlyDictionary<string, string>? knownSecrets)
    {
        _secretSource = knownSecrets;
        RefreshSecrets();
    }

    /// <summary>
    ///     <para>在出口处理前读取当前秘密集合，保留切换前后密钥的脱敏能力。</para>
    ///     <para>Reads the current secret set before output processing to cover keys used before and after a switch.</para>
    /// </summary>
    private void RefreshSecrets()
    {
        _secrets = (_secretSource?.Values ?? [])
                  .Where(value => !string.IsNullOrEmpty(value))
                  .Distinct(StringComparer.Ordinal)
                  .OrderByDescending(value => value.Length)
                  .ToArray();
        _maximumSecretLength = _secrets.Count == 0 ? 0 : _secrets.Max(value => value.Length);
    }

    /// <summary>
    ///     <para>当前来源是否包含已知密钥。</para>
    ///     <para>Whether the current source contains known keys.</para>
    /// </summary>
    public bool HasSecrets
    {
        get
        {
            RefreshSecrets();
            return _secrets.Count > 0;
        }
    }

    /// <summary>
    ///     整段替换全部已知密钥；用于审批摘要、错误说明等一次成形的展示文本。
    ///     Replaces every known secret in a whole string; used for display texts that
    ///     arrive in one piece, such as approval summaries and error details.
    /// </summary>
    public string Redact(string text)
    {
        if (!HasSecrets || string.IsNullOrEmpty(text)) return text;

        foreach (var secret in _secrets) text = text.Replace(secret, Replacement, StringComparison.Ordinal);

        return text;
    }

    /// <summary>
    ///     推送一个流式片段并返回可立即显示的部分：与上一段保留的尾部拼接后替换密钥，
    ///     只输出尾部之后的安全前缀。
    ///     Pushes one streaming fragment and returns the part safe to display: the held
    ///     back tail is prepended, secrets are replaced, and only the safe prefix beyond
    ///     the new tail is emitted.
    /// </summary>
    public string Push(string delta)
    {
        if (!HasSecrets) return delta;

        _pending += delta;
        var processLimit = Math.Max(0, _pending.Length - _maximumSecretLength + 1);
        return processLimit == 0 ? string.Empty : RedactPrefix(processLimit);
    }

    /// <summary>
    ///     流中途边界冲刷（行事件等把同一流式行关掉的时刻）：返回当前可安全显示的部分，
    ///     并保留最长的、仍可能是某密钥严格前缀的尾部——流尚未结束，该尾部可能在后续增量
    ///     中补全为密钥，输出它会把前半段明文放上屏。
    ///     Flushes at a mid-stream boundary (a line event retires the streamed line):
    ///     returns what is safe to display now and holds back the longest tail that could
    ///     still be a strict prefix of a secret — the stream has not ended, and that tail
    ///     may complete into a secret in a later delta.
    /// </summary>
    public string FlushSafe()
    {
        if (!HasSecrets || _pending.Length == 0) return string.Empty;

        return RedactPrefix(_pending.Length - HeldBackSuffixLength());
    }

    /// <summary>
    ///     结束流式输入并返回剩余缓冲。必须先做全量扫描：尾部长度可达最长密钥减一，但更短的
    ///     密钥仍可能完整落在其中——直接输出会泄漏，冲刷前统一替换。
    ///     Ends the streaming input and returns the remaining buffer. A full scan is
    ///     mandatory first: the tail can be as long as the longest secret minus one, yet a
    ///     shorter secret may still sit complete inside it — emitting it raw would leak,
    ///     so the flush replaces everything it returns.
    /// </summary>
    public string Flush()
    {
        if (!HasSecrets || _pending.Length == 0) return string.Empty;

        return RedactPrefix(_pending.Length);
    }

    /// <summary>
    ///     计算缓冲尾部中最长的、仍是某密钥严格前缀（比该密钥短）的后缀长度；这些字符留待
    ///     与下一段拼接匹配。完整落在尾部内的密钥不算——它由扫描替换，不靠保留。
    ///     Computes the longest suffix of the buffer that is still a strict prefix (shorter
    ///     than the secret itself) of some secret; those characters stay for matching
    ///     against the next delta. A secret completed inside the tail does not count — the
    ///     scan replaces it instead of holding it.
    /// </summary>
    private int HeldBackSuffixLength()
    {
        var maximum = Math.Min(_pending.Length, _maximumSecretLength - 1);
        for (var length = maximum; length > 0; length--)
            if (_secrets.Any(value => value.Length > length &&
                                      value.AsSpan(0, length)
                                           .SequenceEqual(_pending.AsSpan(_pending.Length - length, length))))
                return length;

        return 0;
    }

    /// <summary>
    ///     替换缓冲区前 <paramref name="processLimit" /> 个字符中的密钥，保留之后可能构成
    ///     跨片段密钥前缀的尾部。
    ///     Redacts secrets within the first <paramref name="processLimit" /> buffered
    ///     characters, holding back the tail that could still be a cross-fragment
    ///     secret prefix.
    /// </summary>
    private string RedactPrefix(int processLimit)
    {
        var output = new StringBuilder(processLimit);
        var index  = 0;
        while (index < processLimit)
        {
            var secret = _secrets.FirstOrDefault(value => index + value.Length <= _pending.Length &&
                                                          _pending.AsSpan(index, value.Length)
                                                                  .SequenceEqual(value));
            if (secret is not null)
            {
                output.Append(Replacement);
                index += secret.Length;
            }
            else
            {
                output.Append(_pending[index++]);
            }
        }

        _pending = _pending[index..];
        return output.ToString();
    }
}
