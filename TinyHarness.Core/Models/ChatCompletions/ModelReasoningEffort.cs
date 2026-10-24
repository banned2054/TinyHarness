namespace TinyHarness.Core.Models.ChatCompletions;

/// <summary>
///     <para>现有传输适配器可表达的请求强度；是否被具体模型支持由可信调用方确认。</para>
///     <para>Request efforts supported by the transport adapter; callers verify support for the selected model.</para>
/// </summary>
public enum ModelReasoningEffort
{
    None,
    Low,
    Medium,
    High,
    ExtraHigh
}

/// <summary>
///     <para>
///         <see cref="ModelReasoningEffort" /> 字符串值的解析与规范写出。用户配置、目录校验与 CLI 展示共用同一份
///         映射，各入口的档位标识不会漂移；该枚举是当前适配器已实现的映射集合，模型实际支持哪些档位由各模型的能力声明决定。
///     </para>
///     <para>
///         Parses and canonically writes <see cref="ModelReasoningEffort" /> string values. The user config, catalog
///         validation, and CLI display share one mapping so effort ids never drift across entry points; the enum is the
///         set of mappings the adapter implements, while the levels a concrete model supports come from its capability
///         declaration.
///     </para>
/// </summary>
public static class ModelReasoningEffortParser
{
    /// <summary>
    ///     <para>关闭推理的规范 JSON 值。The canonical JSON value for disabling reasoning.</para>
    /// </summary>
    public const string NoneValue = "none";

    /// <summary>
    ///     <para>低强度的规范 JSON 值。The canonical JSON value for the low level.</para>
    /// </summary>
    public const string LowValue = "low";

    /// <summary>
    ///     <para>中等强度的规范 JSON 值。The canonical JSON value for the medium level.</para>
    /// </summary>
    public const string MediumValue = "medium";

    /// <summary>
    ///     <para>高强度的规范 JSON 值。The canonical JSON value for the high level.</para>
    /// </summary>
    public const string HighValue = "high";

    /// <summary>
    ///     <para>最高强度的规范 JSON 值。The canonical JSON value for the extra-high level.</para>
    /// </summary>
    public const string ExtraHighValue = "extra-high";

    /// <summary>
    ///     <para>错误消息中列出的全部可接受档位值。All accepted effort values, listed in error messages.</para>
    /// </summary>
    public const string AcceptedValuesText = "none, low, medium, high, extra-high";

    /// <summary>
    ///     <para>大小写不敏感地解析档位标识；接受规范 kebab-case 值与关闭推理的同义词 "off"。</para>
    ///     <para>Parses an effort id case-insensitively; accepts the canonical kebab-case values and the "off" synonym for none.</para>
    /// </summary>
    public static bool TryParse(string? text, out ModelReasoningEffort value)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case NoneValue :
            case "off" :
                value = ModelReasoningEffort.None;
                return true;
            case LowValue :
                value = ModelReasoningEffort.Low;
                return true;
            case MediumValue :
                value = ModelReasoningEffort.Medium;
                return true;
            case HighValue :
                value = ModelReasoningEffort.High;
                return true;
            case ExtraHighValue :
                value = ModelReasoningEffort.ExtraHigh;
                return true;
            default :
                value = ModelReasoningEffort.None;
                return false;
        }
    }

    /// <summary>
    ///     <para>写出规范 kebab-case 值，与读取端互逆。</para>
    ///     <para>Writes the canonical kebab-case value, the inverse of the reading side.</para>
    /// </summary>
    public static string ToValueString(ModelReasoningEffort value) => value switch
    {
        ModelReasoningEffort.None => NoneValue,
        ModelReasoningEffort.Low => LowValue,
        ModelReasoningEffort.Medium => MediumValue,
        ModelReasoningEffort.High => HighValue,
        ModelReasoningEffort.ExtraHigh => ExtraHighValue,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown model reasoning effort.")
    };
}
