using System.Net.Http;
using System.Text.Json;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Desktop.Services;

namespace TinyHarness.Desktop.ViewModels;

public sealed partial class CatalogProviderEditorViewModel
{
    private          CancellationTokenSource?                       _discoveryCancellation;
    private          bool                                           _isModelPickerOpen;
    private          bool                                           _isFetchingModels;
    private          string                                         _modelDiscoveryStatus = string.Empty;
    private          string                                         _modelSearch          = string.Empty;
    private          IReadOnlyList<CatalogDiscoveredModelViewModel> _discoveredModels    = [];
    private          int                                            _loadingDotCount      = 1;
    private readonly Dictionary<string, CatalogModelEntryViewModel> _removedModelEntries = new(StringComparer.Ordinal);

    internal Func<string, string?, CancellationToken, Task<IReadOnlyList<string>>> FetchModels { get; set; } =
        ProviderModelDiscovery.FetchAsync;

    public AsyncCommand FetchModelsCommand        { get; }
    public RelayCommand SaveModelSelectionCommand { get; }
    public RelayCommand CloseModelPickerCommand   { get; }
    public RelayCommand SelectAllModelsCommand    { get; }

    public bool IsModelPickerOpen
    {
        get => _isModelPickerOpen;
        set
        {
            if (!SetProperty(ref _isModelPickerOpen, value)) return;
            if (!value) _discoveryCancellation?.Cancel();
            OnPropertyChanged(nameof(CanSaveModelSelection));
            SaveModelSelectionCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsFetchingModels
    {
        get => _isFetchingModels;
        private set
        {
            if (!SetProperty(ref _isFetchingModels, value)) return;
            if (value) _loadingDotCount = 1;
            OnPropertyChanged(nameof(ModelListMessage));
            OnPropertyChanged(nameof(CanSaveModelSelection));
            SaveModelSelectionCommand.RaiseCanExecuteChanged();
        }
    }

    public string ModelDiscoveryStatus
    {
        get => _modelDiscoveryStatus;
        private set => SetProperty(ref _modelDiscoveryStatus, value);
    }

    public string ModelSearch
    {
        get => _modelSearch;
        set
        {
            if (!SetProperty(ref _modelSearch, value)) return;
            OnPropertyChanged(nameof(FilteredDiscoveredModels));
            OnPropertyChanged(nameof(HasFilteredModels));
            NotifyModelSelectionChanged();
        }
    }

    public IReadOnlyList<CatalogDiscoveredModelViewModel> FilteredDiscoveredModels =>
        _discoveredModels.Where(model => model.Id.Contains(ModelSearch.Trim(), StringComparison.OrdinalIgnoreCase))
                         .ToArray();

    public int    ModelsToAddCount      => _discoveredModels.Count(model => model.IsSelected  && !model.IsAlreadyAdded);
    public int    ModelsToRemoveCount   => _discoveredModels.Count(model => !model.IsSelected && model.IsAlreadyAdded);
    public string ModelSelectionSummary => $"新增 {ModelsToAddCount} 个，移除 {ModelsToRemoveCount} 个";

    public bool CanSaveModelSelection => IsModelPickerOpen &&
                                         !IsFetchingModels &&
                                         CanEdit &&
                                         (ModelsToAddCount > 0 || ModelsToRemoveCount > 0);

    public bool HasFilteredModels => FilteredDiscoveredModels.Count > 0;

    public string ModelListMessage => IsFetchingModels
        ? "正在获取模型" + new string('.', _loadingDotCount)
        : "暂无可选结果";

    public string SelectAllModelsText => HasFilteredModels && FilteredDiscoveredModels.All(model => model.IsSelected)
        ? "取消全选"
        : "全选";

    /// <summary>
    ///     <para>显式点击后才读取凭据和请求模型列表；关闭弹层或更改连接设置会取消并丢弃迟到结果。</para>
    ///     <para>
    ///         Reads credentials and requests models only on an explicit click; closing or changing connection settings
    ///         cancels and discards late results.
    ///     </para>
    /// </summary>
    internal async Task FetchModelsAsync()
    {
        if (!CanEdit || IsFetchingModels) return;
        IsModelPickerOpen = true;
        _discoveredModels = [];
        ModelSearch       = string.Empty;
        NotifyDiscoveredModelsChanged();
        using var cancellation = new CancellationTokenSource();
        _discoveryCancellation = cancellation;
        IsFetchingModels       = true;
        ModelDiscoveryStatus   = "正在获取模型…";
        try
        {
            var endpoint = Endpoint.Trim();
            _ = ProviderModelDiscovery.ModelsUri(endpoint);
            var key = Secret;
            if (string.IsNullOrEmpty(key))
            {
                if (!string.IsNullOrWhiteSpace(EnvironmentVariable) && !string.IsNullOrWhiteSpace(CredentialTarget))
                {
                    ModelDiscoveryStatus = "凭据引用请只填写环境变量或系统凭据目标中的一种。";
                    return;
                }

                try
                {
                    key = ApiKeyReader.Read(EnvironmentVariable.Trim(), CredentialTarget.Trim(), _credentials);
                }
                catch (Exception)
                {
                    ModelDiscoveryStatus = "无法读取 API 密钥，请检查凭据来源或在上方填写密钥。";
                    return;
                }

                if (key is null && (!string.IsNullOrWhiteSpace(EnvironmentVariable) ||
                                    !string.IsNullOrWhiteSpace(CredentialTarget)))
                {
                    ModelDiscoveryStatus = "未找到 API 密钥，请检查凭据来源或在上方填写密钥。";
                    return;
                }
            }

            var ids = await FetchModels(endpoint, key, cancellation.Token);
            if (cancellation.IsCancellationRequested || !IsModelPickerOpen) return;
            var existing = ModelEntries.Select(entry => entry.Id.Trim()).ToHashSet(StringComparer.Ordinal);
            _discoveredModels = ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                                   .Select(id => new CatalogDiscoveredModelViewModel(id, existing.Contains(id),
                                               NotifyModelSelectionChanged))
                                   .ToArray();
            ModelDiscoveryStatus = ids.Count == 0
                ? "服务未返回可用的模型。"
                : $"找到 {_discoveredModels.Count} 个模型，已添加 {_discoveredModels.Count(model => model.IsAlreadyAdded)} 个。";
            NotifyDiscoveredModelsChanged();
        }
        catch (OperationCanceledException)
        {
            if (!cancellation.IsCancellationRequested) ModelDiscoveryStatus = "获取模型超时，请重试。";
        }
        catch (InvalidDataException exception)
        {
            ModelDiscoveryStatus = Redact(exception.Message);
        }
        catch (JsonException)
        {
            ModelDiscoveryStatus = "接口返回的内容不是有效的模型列表 JSON。";
        }
        catch (HttpRequestException)
        {
            ModelDiscoveryStatus = "无法连接模型列表接口，请检查地址与网络后重试。";
        }
        catch (Exception)
        {
            ModelDiscoveryStatus = "获取模型失败，请检查接口配置后重试。";
        }
        finally
        {
            _discoveryCancellation = null;
            IsFetchingModels       = false;
        }
    }

    internal void CloseModelPicker() => IsModelPickerOpen = false;

    /// <summary>
    ///     <para>仅在模型面板正在获取时推进一至三个点的加载提示。</para>
    ///     <para>Advances the loading indicator through one to three dots only while the model panel is fetching.</para>
    /// </summary>
    internal void AdvanceLoadingIndicator()
    {
        if (!IsFetchingModels || !IsModelPickerOpen) return;
        _loadingDotCount = _loadingDotCount % 3 + 1;
        OnPropertyChanged(nameof(ModelListMessage));
    }

    /// <summary>
    ///     <para>将明确勾选的增删应用到表单草稿；保留未返回项与已有参数，最终保存仍走整卡事务。</para>
    ///     <para>
    ///         Applies explicit selection changes to the draft, preserving unreturned models and existing parameters;
    ///         persistence still uses the whole-card transaction.
    ///     </para>
    /// </summary>
    private void SaveModelSelection()
    {
        if (!CanSaveModelSelection) return;
        var removed = _discoveredModels.Where(model => model.IsAlreadyAdded && !model.IsSelected)
                                       .Select(model => model.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in ModelEntries.Where(entry => removed.Contains(entry.Id.Trim())).ToArray())
        {
            entry.PropertyChanged -= OnEntryPropertyChanged;
            _removedModelEntries[entry.Id.Trim()] = entry;
            ModelEntries.Remove(entry);
        }
        if (removed.Contains(_defaultModelId)) SelectDefaultModel(string.Empty);

        var existing = ModelEntries.Select(entry => entry.Id.Trim()).ToHashSet(StringComparer.Ordinal);
        foreach (var model in _discoveredModels.Where(model => model.IsSelected && !model.IsAlreadyAdded))
        {
            if (!existing.Add(model.Id)) continue;
            if (!_removedModelEntries.Remove(model.Id, out var entry))
            {
                entry = new CatalogModelEntryViewModel(null, NotifyFormChanged)
                {
                    Id          = model.Id,
                    WindowDraft = CatalogModelEntryViewModel.FormatTokenCount(new TinyHarnessConfig().ContextWindowTokens)
                };
            }
            AttachEntry(entry);
            ModelEntries.Add(entry);
        }

        CloseModelPicker();
        RefreshDefaultModelOptions();
        NotifyFormChanged();
        OnPropertyChanged(nameof(HasModelEntries));
    }

    private void NotifyDiscoveredModelsChanged()
    {
        OnPropertyChanged(nameof(FilteredDiscoveredModels));
        OnPropertyChanged(nameof(HasFilteredModels));
        NotifyModelSelectionChanged();
    }

    private void NotifyModelSelectionChanged()
    {
        OnPropertyChanged(nameof(ModelsToAddCount));
        OnPropertyChanged(nameof(ModelsToRemoveCount));
        OnPropertyChanged(nameof(ModelSelectionSummary));
        OnPropertyChanged(nameof(CanSaveModelSelection));
        OnPropertyChanged(nameof(SelectAllModelsText));
        SaveModelSelectionCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    ///     <para>切换当前搜索结果的勾选状态，保留筛选外的选择；已有模型也可取消。</para>
    ///     <para>
    ///         Toggles current search results while preserving selections outside the filter; existing models can be
    ///         unchecked.
    ///     </para>
    /// </summary>
    private void SelectAllModels()
    {
        if (IsFetchingModels) return;
        var models                                     = FilteredDiscoveredModels;
        var select                                     = models.Any(model => !model.IsSelected);
        foreach (var model in models) model.IsSelected = select;
    }
}

/// <summary>
///     <para>发现列表中的模型与打开时的目录归属；已有项默认勾选，可显式取消以移除。</para>
///     <para>
///         A discovered model and its initial catalog membership; existing entries start checked and can be unchecked
///         for removal.
///     </para>
/// </summary>
public sealed class CatalogDiscoveredModelViewModel(string id, bool isAlreadyAdded, Action changed) : ObservableObject
{
    private bool _isSelected = isAlreadyAdded;

    public string Id             { get; } = id;
    public bool   IsAlreadyAdded { get; } = isAlreadyAdded;
    public string Label          => IsAlreadyAdded ? $"{Id}（已添加）" : Id;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value)) return;
            changed();
        }
    }
}
