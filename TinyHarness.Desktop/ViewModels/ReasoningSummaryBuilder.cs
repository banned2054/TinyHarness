using System.Text;

namespace TinyHarness.Desktop.ViewModels;

/// <summary>
///     <para>增量提取去掉双星号后的首行摘要，仅保存有界前缀并保留跨片段标记状态。</para>
///     <para>
///         Incrementally extracts the first-line summary without double asterisks, retaining a bounded prefix and
///         cross-fragment marker state.
///     </para>
/// </summary>
internal sealed class ReasoningSummaryBuilder
{
    private const    int           Limit   = 80;
    private readonly StringBuilder _prefix = new(Limit);
    private          bool          _ended;
    private          long          _lastNonWhitespace;
    private          long          _length;
    private          bool          _pendingStar;

    internal string Text
    {
        get
        {
            var length                                        = _pendingStar ? _length + 1 : _lastNonWhitespace;
            var prefix                                        = _prefix.ToString();
            if (_pendingStar && prefix.Length < Limit) prefix += '*';
            return length                     > Limit ? prefix + "…" : prefix[..(int)length];
        }
    }

    /// <summary>
    ///     <para>处理新片段；首行结束后不再扫描后续正文。</para>
    ///     <para>Processes a fragment and stops scanning body text after the first line ends.</para>
    /// </summary>
    internal void Append(string text)
    {
        if (_ended) return;
        foreach (var character in text)
        {
            if (character == '*')
            {
                _pendingStar = !_pendingStar;
                continue;
            }

            if (_pendingStar)
            {
                AppendCharacter('*');
                _pendingStar = false;
            }

            if (character == '\n')
            {
                _ended = true;
                return;
            }

            AppendCharacter(character);
        }
    }

    private void AppendCharacter(char character)
    {
        if (_length == 0 && char.IsWhiteSpace(character)) return;
        _length++;
        if (_prefix.Length < Limit) _prefix.Append(character);
        if (!char.IsWhiteSpace(character)) _lastNonWhitespace = _length;
    }
}
