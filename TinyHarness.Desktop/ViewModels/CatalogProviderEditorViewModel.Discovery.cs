using System.Net.Http;
using System.Text.Json;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Desktop.Services;

namespace TinyHarness.Desktop.ViewModels;

public sealed partial class CatalogProviderEditorViewModel
{
    private CancellationTokenSource?                       _discoveryCancellation;
    private bool                                           _isModelPickerOpen;
    private bool                                           _isFetchingModels;
    private string                                         _modelDiscoveryStatus = string.Empty;
    private string                                         _modelSearch          = string.Empty;
    private IReadOnlyList<CatalogDiscoveredModelViewModel> _discoveredModels = [];

    internal Func<string, string?, CancellationToken, Task<IReadOnlyList<string>>> FetchModels { get; set; } =
        ProviderModelDiscovery.FetchAsync;

    public AsyncCommand FetchModelsCommand       { get; }
    public RelayCommand AddSelectedModelsCommand { get; }
    public RelayCommand CloseModelPickerCommand  { get; }

    public bool IsModelPickerOpen
    {
        get => _isModelPickerOpen;
        set
        {
            if (!SetProperty(ref _isModelPickerOpen, value)) return;
            if (!value) _discoveryCancellation?.Cancel();
            OnPropertyChanged(nameof(CanAddSelectedModels));
            AddSelectedModelsCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsFetchingModels
    {
        get => _isFetchingModels;
        private set
        {
            if (!SetProperty(ref _isFetchingModels, value)) return;
            OnPropertyChanged(nameof(CanAddSelectedModels));
            AddSelectedModelsCommand.RaiseCanExecuteChanged();
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
            if (SetProperty(ref _modelSearch, value)) OnPropertyChanged(nameof(FilteredDiscoveredModels));
        }
    }

    public IReadOnlyList<CatalogDiscoveredModelViewModel> FilteredDiscoveredModels =>
        _discoveredModels.Where(model => model.Id.Contains(ModelSearch.Trim(), StringComparison.OrdinalIgnoreCase))
                         .ToArray();

    public int    SelectedModelCount    => _discoveredModels.Count(model => model.IsSelected && !model.IsAlreadyAdded);
    public string AddSelectedModelsText => $"添加所选（{SelectedModelCount}）";
    public bool   CanAddSelectedModels  => IsModelPickerOpen && !IsFetchingModels && CanEdit && SelectedModelCount > 0;

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
            var available = _discoveredModels.Count(model => !model.IsAlreadyAdded);
            ModelDiscoveryStatus = ids.Count == 0 ? "服务未返回可用的模型。"
                : available                  == 0 ? "获取到的模型均已添加。"
                                                    : $"找到 {_discoveredModels.Count} 个模型，{available} 个可添加。";
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
    ///     <para>只追加尚不存在的勾选项到表单草稿；窗口沿用应用默认值，能力保持未声明，保存仍走整卡事务。</para>
    ///     <para>
    ///         Appends checked missing IDs to the draft using the app's default window and undeclared capabilities; saving
    ///         still uses the whole-card transaction.
    ///     </para>
    /// </summary>
    private void AddSelectedModels()
    {
        if (!CanAddSelectedModels) return;
        var existing = ModelEntries.Select(entry => entry.Id.Trim()).ToHashSet(StringComparer.Ordinal);
        foreach (var model in _discoveredModels.Where(model => model.IsSelected && !model.IsAlreadyAdded))
        {
            if (!existing.Add(model.Id)) continue;
            var entry = new CatalogModelEntryViewModel(null, NotifyFormChanged)
            {
                Id          = model.Id,
                WindowDraft = CatalogModelEntryViewModel.FormatTokenCount(new TinyHarnessConfig().ContextWindowTokens)
            };
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
        NotifyModelSelectionChanged();
    }

    private void NotifyModelSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedModelCount));
        OnPropertyChanged(nameof(AddSelectedModelsText));
        OnPropertyChanged(nameof(CanAddSelectedModels));
        AddSelectedModelsCommand.RaiseCanExecuteChanged();
    }
}

/// <summary>
///     <para>发现列表中的可选 ID；已有模型只显示状态，不可重复勾选。</para>
///     <para>A selectable discovered ID; existing models show their status and cannot be selected again.</para>
/// </summary>
public sealed class CatalogDiscoveredModelViewModel(string id, bool isAlreadyAdded, Action changed) : ObservableObject
{
    private bool _isSelected;

    public string Id             { get; } = id;
    public bool   IsAlreadyAdded { get; } = isAlreadyAdded;
    public string Label          => IsAlreadyAdded ? $"{Id}（已添加）" : Id;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (IsAlreadyAdded || !SetProperty(ref _isSelected, value)) return;
            changed();
        }
    }
}
