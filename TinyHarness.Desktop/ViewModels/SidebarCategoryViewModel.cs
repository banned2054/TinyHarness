namespace TinyHarness.Desktop.ViewModels;

/// <summary>
///     <para>侧栏固定分类标题及其可选展开状态。</para>
///     <para>A fixed sidebar category heading and its optional expansion state.</para>
/// </summary>
public sealed class SidebarCategoryViewModel(string key, string title, bool canCollapse = true) : ObservableObject
{
    private bool _isExpanded = true;

    /// <summary>
    ///     <para>固定侧栏分类的稳定键。</para>
    ///     <para>The stable key for a fixed sidebar category.</para>
    /// </summary>
    public string Key { get; } = key;

    /// <summary>
    ///     <para>分类标题。</para>
    ///     <para>The category title.</para>
    /// </summary>
    public string Title { get; } = title;

    /// <summary>
    ///     <para>分类是否允许折叠。</para>
    ///     <para>Whether the category can be collapsed.</para>
    /// </summary>
    public bool CanCollapse { get; } = canCollapse;

    /// <summary>
    ///     <para>包含分类当前展开状态的辅助技术名称。</para>
    ///     <para>The assistive-technology name including the category's current expansion state.</para>
    /// </summary>
    public string AccessibilityName => IsExpanded ? $"{Title}，已展开" : $"{Title}，已折叠";

    /// <summary>
    ///     <para>分类是否展开。</para>
    ///     <para>Whether the category is expanded.</para>
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            OnPropertyChanged(nameof(AccessibilityName));
        }
    }
}
