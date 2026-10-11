using System.Text;

namespace TinyHarness.Cli.Services;

/// <summary>
///     终端展示文本的统一格式化：先做已知密钥脱敏（可选），归一 CRLF，再保留换行与制表符、
///     转义其他控制字符。事件展示、审批界面与 chat 摘要共用同一条规则，避免多份副本各自
///     漂移。
///     One formatting rule for terminal display text: redact known secrets (optional),
///     normalize CRLF, then preserve newlines/tabs while escaping other control
///     characters. The event display, the approval UI and the chat summary share it so
///     their copies cannot drift.
/// </summary>
internal static class TerminalDisplay
{
    /// <summary>
    ///     格式化一段展示文本；传入 <paramref name="redactor" /> 时先做已知密钥脱敏，流式
    ///     增量已在 <see cref="DisplaySecretRedactor.Push" /> 中替换过时可不传，避免重复扫描。
    ///     Formats one display text; when <paramref name="redactor" /> is given the known
    ///     secrets are redacted first. Streaming deltas already replaced by
    ///     <see cref="DisplaySecretRedactor.Push" /> pass no redactor, skipping a redundant scan.
    /// </summary>
    public static string Format(string text, DisplaySecretRedactor? redactor = null)
    {
        if (redactor is not null) text = redactor.Redact(text);

        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var display = new StringBuilder(text.Length);
        foreach (var character in text)
            if (char.IsControl(character) && character is not '\n' and not '\t')
                display.Append("\\u").Append(((int)character).ToString("X4"));
            else
                display.Append(character);

        return display.ToString();
    }
}
