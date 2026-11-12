using System.Text;
using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Persistence;

/// <summary>
///     <para>会话名称与待生成标记；没有名称时继续显示首条用户输入摘要。</para>
///     <para>A session name and pending-generation marker; unnamed sessions keep the first-input preview.</para>
/// </summary>
public sealed record SessionTitle
{
    public const string CurrentSchema = "tinyharness.session-title/1";
    public const int    MaxCharacters = 36;

    [JsonRequired]
    public string Schema { get; init; } = CurrentSchema;

    [JsonRequired]
    public string? Name { get; init; }

    [JsonRequired]
    public bool Pending { get; init; } = true;

    /// <summary>
    ///     <para>校验名称与状态的一致性，不接受未知 schema 或未经规范化的名称。</para>
    ///     <para>Validates name/state consistency, rejecting unknown schemas and unnormalized names.</para>
    /// </summary>
    public void Validate()
    {
        if (Schema != CurrentSchema ||
            Pending != (Name is null) ||
            (Name is not null && (Name.Length == 0 || Name != Normalize(Name))))
            throw new InvalidDataException("Invalid session title state.");
    }

    /// <summary>
    ///     <para>压缩空白、去除包围引号与末尾标点，按 Unicode 字符限制长度。</para>
    ///     <para>Collapses whitespace, trims surrounding quotes and terminal punctuation, and limits Unicode scalars.</para>
    /// </summary>
    public static string Normalize(string text)
    {
        var value = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                          .TrimEnd('.', '!', '?', '。', '！', '？')
                          .Trim('"', '\'', '`', '“', '”', '‘', '’').Trim().TrimEnd('.', '!', '?', '。', '！', '？');
        var result = new StringBuilder();
        foreach (var rune in value.EnumerateRunes().Take(MaxCharacters)) result.Append(rune.ToString());
        return result.ToString().TrimEnd().TrimEnd('.', '!', '?', '。', '！', '？');
    }
}
