using Avalonia;
using Avalonia.Styling;

namespace TinyHarness.Desktop.ViewModels;

/// <summary>
///     <para>
///         应用内居中设置面板：分区导航、加载与错误态、关闭请求；关闭只发事件，
///         由宿主（MainWindowViewModel）收起面板。目录分区承担全部配置写入。
///     </para>
///     <para>
///         The in-app centered settings panel: section navigation, loading and error states, and the close
///         request. Closing only raises an event for the host (MainWindowViewModel) to hide the panel;
///         the catalog section owns all configuration writes.
///     </para>
/// </summary>
public sealed class SettingsPanelViewModel : ObservableObject
{
    public SettingsPanelViewModel(GeneralSettingsSectionViewModel general, CatalogSettingsViewModel catalog)
    {
        General  = general;
        Catalog  = catalog;
        Sections = [General, Catalog];
        foreach (var section in Sections) section.SelectCommand = new RelayCommand(() => SelectSection(section));
        ActiveSection = General;
        CloseCommand  = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        RetryCommand  = new AsyncCommand(() => OpenAsync(), ShowError);
    }

    public GeneralSettingsSectionViewModel General { get; }

    public CatalogSettingsViewModel Catalog { get; }

    public IReadOnlyList<SettingsSectionViewModel> Sections { get; }

    public RelayCommand CloseCommand { get; }

    public AsyncCommand RetryCommand { get; }

    /// <summary>
    ///     <para>关闭请求（关闭按钮、Esc 或遮罩点击触发）；宿主订阅后把面板置为不可见。</para>
    ///     <para>The host hides the panel when the close button, Escape key or mask requests closing.</para>
    /// </summary>
    public event EventHandler? CloseRequested;

    private SettingsSectionViewModel? _activeSection;
    private bool _isLoading = true;
    private bool _hasError;
    private string _errorText = string.Empty;

    public SettingsSectionViewModel ActiveSection
    {
        get => _activeSection ?? General;
        private set
        {
            if (!SetProperty(ref _activeSection, value)) return;
            foreach (var section in Sections)
            {
                section.IsSelected = ReferenceEquals(section, value);
                section.IsActive   = ReferenceEquals(section, value);
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value)) OnPropertyChanged(nameof(IsReady));
        }
    }

    public bool HasError
    {
        get => _hasError;
        private set
        {
            if (SetProperty(ref _hasError, value)) OnPropertyChanged(nameof(IsReady));
        }
    }

    public bool IsReady => !IsLoading && !HasError;

    public string ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    /// <summary>
    ///     <para>打开面板：重新读取目录并复位错误态；失败转入错误态等待重试，不抛出。</para>
    ///     <para>Opens the panel by reloading the catalog and clearing errors; failures enter the error state for retry instead of throwing.</para>
    /// </summary>
    public async Task OpenAsync()
    {
        IsLoading = true;
        HasError  = false;
        try
        {
            await Catalog.LoadAsync();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    ///     <para>切换分区；目录分区的展开卡与草稿由其自身在重载时处理，此处只切换可见性。</para>
    ///     <para>Switches section visibility; the catalog manages its open cards and drafts during reload.</para>
    /// </summary>
    public void SelectSection(SettingsSectionViewModel section)
    {
        if (ReferenceEquals(ActiveSection, section)) return;

        ActiveSection = section;
    }

    private void ShowError(Exception exception)
    {
        ErrorText = $"设置加载失败：{exception.Message}";
        HasError  = true;
    }
}

/// <summary>
///     <para>设置分区导航项：标题固定，选中与激活状态由面板壳同步。</para>
///     <para>A settings section navigation item: fixed title with selection and activation state synced by the panel shell.</para>
/// </summary>
public abstract class SettingsSectionViewModel : ObservableObject
{
    protected SettingsSectionViewModel(string title) => Title = title;

    public string Title { get; }

    public RelayCommand? SelectCommand { get; internal set; }

    private bool _isSelected;
    private bool _isActive;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    ///     <para>分区内容是否可见；与选中态分离，避免导航项样式依赖内容可见性。</para>
    ///     <para>Whether section content is visible, independently of navigation styling.</para>
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
}

/// <summary>
///     <para>通用分区：主题选择与配置文件路径展示；本分区不写入用户配置。</para>
///     <para>The general section: theme choice and config path display; this section never writes the user config.</para>
/// </summary>
public sealed class GeneralSettingsSectionViewModel : SettingsSectionViewModel
{
    public GeneralSettingsSectionViewModel(string configPath) : base("通用")
    {
        ConfigPath         = configPath;
        SelectLightCommand = new RelayCommand(() => SelectTheme(ThemeVariant.Light));
        SelectDarkCommand  = new RelayCommand(() => SelectTheme(ThemeVariant.Dark));
    }

    public string ConfigPath { get; }

    public RelayCommand SelectLightCommand { get; }

    public RelayCommand SelectDarkCommand { get; }

    // 主题由 Application 全局承担；不引入第二份状态，选中态即时读取当前生效变体。
    public bool IsLightSelected => Application.Current?.ActualThemeVariant != ThemeVariant.Dark;

    public bool IsDarkSelected => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    private void SelectTheme(ThemeVariant variant)
    {
        if (Application.Current is { } application) application.RequestedThemeVariant = variant;
        OnPropertyChanged(nameof(IsLightSelected));
        OnPropertyChanged(nameof(IsDarkSelected));
    }
}
