namespace TinyHarness.Desktop.Models;

/// <summary>
///     <para>输入区模型与推理等级选择弹层的展示样式。</para>
///     <para>Presentation style of the composer model and reasoning-effort picker popup.</para>
/// </summary>
public enum ModelPickerStyle
{
    /// <summary>紧凑滑块卡片：默认展开强度滑块与模型切换入口。</summary>
    CompactSlider,

    /// <summary>一体面板：上方展示模型列表，底部常驻强度滑块。</summary>
    UnifiedPanel,

    /// <summary>经典列表：两级导航列表。</summary>
    ClassicList
}

/// <summary>
///     <para>在模型选择器样式枚举与持久化字符串值之间转换；未知值回退到默认紧凑滑块。</para>
///     <para>Converts between <see cref="ModelPickerStyle" /> and persisted string values, falling back to compact slider.</para>
/// </summary>
internal static class ModelPickerStyleCodec
{
    public const string CompactSliderValue = "compact-slider";
    public const string UnifiedPanelValue  = "unified-panel";
    public const string ClassicListValue   = "classic-list";

    public static ModelPickerStyle Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        UnifiedPanelValue => ModelPickerStyle.UnifiedPanel,
        ClassicListValue  => ModelPickerStyle.ClassicList,
        _                 => ModelPickerStyle.CompactSlider
    };

    public static string ToValue(ModelPickerStyle style) => style switch
    {
        ModelPickerStyle.UnifiedPanel => UnifiedPanelValue,
        ModelPickerStyle.ClassicList  => ClassicListValue,
        _                             => CompactSliderValue
    };
}
