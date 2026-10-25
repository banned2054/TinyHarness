using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Desktop.ViewModels;

/// <summary>
///     <para>
///         模型分区：供应商行列表加行内编辑卡与添加卡，一次只展开一张卡。保存经
///         <see cref="ModelCatalogEditor" /> 写入可信用户配置；编辑冲突保留草稿，
///         由用户重开面板后重试。不提供删除：目录管理本期不含删除入口。
///     </para>
///     <para>
///         The models section: provider rows with an inline editor card and the add card, one card open at
///         a time. Saving goes through <see cref="ModelCatalogEditor" /> into the trusted user config;
///         edit conflicts retain drafts for a retry after reopening the panel. No deletion is offered:
///         catalog management has no delete entry in this phase.
///     </para>
/// </summary>
public sealed class CatalogSettingsViewModel : SettingsSectionViewModel
{
    private readonly string _path;
    private readonly Func<Task> _refresh;
    private readonly ICredentialStore _credentials;
    private UserConfig _baseline = new();
    private CatalogProviderRowViewModel? _editingRow;
    private CatalogProviderEditorViewModel? _editor;
    private bool _isAddOpen;
    private bool _isRefreshing;
    private string _sectionNotice = string.Empty;

    /// <summary>
    ///     <para>使用可信用户配置路径与保存后刷新回调建立分区。</para>
    ///     <para>Creates the section for a trusted user config path with a post-save refresh callback.</para>
    /// </summary>
    public CatalogSettingsViewModel(string path, Func<Task> refresh, ICredentialStore? credentials = null)
        : base("模型")
    {
        _path          = path;
        _refresh       = refresh;
        _credentials   = credentials ?? new WindowsCredentialStore();
        OpenAddCommand = new RelayCommand(OpenAdd, () => CanSwitchCards);
    }

    public string Intro =>
        "供应商描述实际调用的服务，其下模型共用接口地址、协议与凭据引用。保存只写入用户配置，不切换已打开会话。";

    public ObservableCollection<CatalogProviderRowViewModel> Providers { get; } = [];

    public bool HasProviders => Providers.Count > 0;

    public RelayCommand OpenAddCommand { get; }

    private bool CanSwitchCards => !_isRefreshing && Editor?.IsSaving != true;

    /// <summary>
    ///     <para>添加卡展开态；与行编辑互斥，一次只有一张卡。</para>
    ///     <para>Whether the add card is open; only one add or edit card can be open at a time.</para>
    /// </summary>
    public bool IsAddOpen
    {
        get => _isAddOpen;
        private set => SetProperty(ref _isAddOpen, value);
    }

    /// <summary>
    ///     <para>当前展开卡的编辑器；行编辑与添加卡共用一个槽位。</para>
    ///     <para>The current editor shared by row editing and the add card.</para>
    /// </summary>
    public CatalogProviderEditorViewModel? Editor
    {
        get => _editor;
        private set
        {
            if (ReferenceEquals(_editor, value)) return;
            if (_editor is not null) _editor.PropertyChanged -= OnEditorPropertyChanged;
            SetProperty(ref _editor, value);
            if (_editor is not null) _editor.PropertyChanged += OnEditorPropertyChanged;
            NotifyCardCommands();
        }
    }

    /// <summary>
    ///     <para>分区级提示（保存结果）；任何卡片打开动作清除。</para>
    ///     <para>Section-level save notice, cleared when any card opens.</para>
    /// </summary>
    public string SectionNotice
    {
        get => _sectionNotice;
        private set
        {
            if (SetProperty(ref _sectionNotice, value)) OnPropertyChanged(nameof(HasSectionNotice));
        }
    }

    public bool HasSectionNotice => _sectionNotice.Length > 0;

    /// <summary>
    ///     <para>重新读取可信用户配置并重建行；展开的卡与未保存草稿一并丢弃。</para>
    ///     <para>Reloads the trusted user config and rebuilds rows; open cards and unsaved drafts are discarded.</para>
    /// </summary>
    public async Task LoadAsync()
    {
        if (!CanSwitchCards) return;
        SetRefreshing(true);
        try
        {
            await ReloadAsync();
        }
        finally
        {
            SetRefreshing(false);
        }
    }

    private async Task ReloadAsync()
    {
        _baseline = await UserConfigStore.LoadAsync(_path, CancellationToken.None);
        CloseCards();
        Providers.Clear();
        foreach (var provider in _baseline.Profiles)
            Providers.Add(new CatalogProviderRowViewModel(provider, _baseline.DefaultProfile == provider.Name,
                                                          OpenEditor, () => CanSwitchCards));
        OnPropertyChanged(nameof(HasProviders));
    }

    /// <summary>
    ///     <para>打开添加卡；已有行编辑卡先收起（草稿丢弃）。</para>
    ///     <para>Opens the add card after closing the current editor and discarding its draft.</para>
    /// </summary>
    private void OpenAdd()
    {
        if (!CanSwitchCards || IsAddOpen) return;

        CloseCards();
        SectionNotice = string.Empty;
        Editor = CatalogProviderEditorViewModel.ForCreate(_path, _credentials, _baseline, OnEditorSaved,
                                                          OnEditorCancelled);
        IsAddOpen = true;
    }

    private void OpenEditor(CatalogProviderRowViewModel row)
    {
        if (!CanSwitchCards) return;
        // 官方语义：同一行再次点击收起；打开其他卡先丢弃当前卡。
        if (!IsAddOpen && ReferenceEquals(_editingRow, row))
        {
            CloseCards();
            return;
        }

        CloseCards();
        SectionNotice = string.Empty;
        _editingRow   = row;
        Editor = CatalogProviderEditorViewModel.ForEdit(_path, _credentials, _baseline, row.Source,
                                                        OnEditorSaved, OnEditorCancelled);
        SyncRows();
    }

    private void CloseCards()
    {
        _editingRow = null;
        Editor      = null;
        IsAddOpen   = false;
        SyncRows();
    }

    private void SyncRows()
    {
        foreach (var row in Providers)
        {
            row.IsEditing = ReferenceEquals(row, _editingRow) && !IsAddOpen;
            row.Editor    = row.IsEditing ? Editor : null;
        }
    }

    private void OnEditorSaved(CatalogProviderEditorViewModel editor)
    {
        if (!ReferenceEquals(Editor, editor)) return;
        SetRefreshing(true);
        CloseCards();
        _ = SavedAsync();
    }

    private void OnEditorCancelled(CatalogProviderEditorViewModel editor)
    {
        if (ReferenceEquals(Editor, editor) && CanSwitchCards) CloseCards();
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(CatalogProviderEditorViewModel.IsSaving)) NotifyCardCommands();
    }

    private void SetRefreshing(bool value)
    {
        _isRefreshing = value;
        NotifyCardCommands();
    }

    private void NotifyCardCommands()
    {
        OpenAddCommand.RaiseCanExecuteChanged();
        foreach (var row in Providers) row.EditCommand.RaiseCanExecuteChanged();
    }

    private async Task SavedAsync()
    {
        try
        {
            try
            {
                await ReloadAsync();
            }
            catch (Exception exception)
            {
                SectionNotice = $"目录已保存，读取最新目录失败：{exception.Message}";
                return;
            }

            try
            {
                await _refresh();
                SectionNotice = "目录已保存并刷新；请回到会话显式选择模型。";
            }
            catch (Exception)
            {
                SectionNotice = "目录已保存，刷新会话目录失败；请手动刷新。";
            }
        }
        finally
        {
            SetRefreshing(false);
        }
    }
}

/// <summary>
///     <para>供应商行：名称、说明、凭据引用圆点与编辑按钮；编辑时行内展开编辑卡。</para>
///     <para>A provider row: name, caption, credential reference dot and the edit button; editing expands the editor card inline.</para>
/// </summary>
public sealed class CatalogProviderRowViewModel : ObservableObject
{
    private readonly Action<CatalogProviderRowViewModel> _onEdit;
    private bool _isEditing;
    private CatalogProviderEditorViewModel? _editor;

    /// <summary>
    ///     <para>建立供应商行；卡片操作是否可用由所属分区统一决定。</para>
    ///     <para>Creates a provider row whose section decides whether card operations are available.</para>
    /// </summary>
    public CatalogProviderRowViewModel(UserProfile                         source, bool       isDefaultProvider,
                                       Action<CatalogProviderRowViewModel> onEdit, Func<bool> canEdit)
    {
        Source            = source;
        IsDefaultProvider = isDefaultProvider;
        _onEdit           = onEdit;
        EditCommand       = new RelayCommand(() => _onEdit(this), canEdit);
    }

    /// <summary>
    ///     <para>行构建时的供应商快照；编辑卡从这里取初始值，冲突检测仍以打开时的完整基线为准。</para>
    ///     <para>The provider snapshot used to initialize editing; conflicts use the full baseline captured on opening.</para>
    /// </summary>
    public UserProfile Source { get; }

    public string Name => Source.Name;

    public string Caption => $"{ChatApiKindParser.ToValueString(Source.ChatApi)} · {Source.Models.Count} 个模型";

    public bool IsDefaultProvider { get; }

    /// <summary>
    ///     <para>凭据圆点：有引用=实心绿，无引用=空心灰（待配置）；不读取密钥值。</para>
    ///     <para>The credential indicator reflects references only and never reads secret values.</para>
    /// </summary>
    public bool IsCredentialSet => Source.ApiKeyEnvironmentVariable.Length > 0 ||
                                   Source.ApiKeyCredentialTarget.Length    > 0;

    public bool IsCredentialUnset => !IsCredentialSet;

    public RelayCommand EditCommand { get; }

    public bool IsEditing
    {
        get => _isEditing;
        internal set => SetProperty(ref _isEditing, value);
    }

    public CatalogProviderEditorViewModel? Editor
    {
        get => _editor;
        internal set
        {
            if (SetProperty(ref _editor, value)) OnPropertyChanged(nameof(HasEditor));
        }
    }

    public bool HasEditor => Editor is not null;
}

/// <summary>
///     <para>
///         供应商编辑卡（添加与编辑共用）：名称（仅创建）、接口地址、协议、凭据引用、
///         新密钥与默认项选择，加其下模型目录草稿。保存一次提交整卡；失败保留全部草稿。
///     </para>
///     <para>
///         The provider editor card shared by add and edit: name (create only), endpoint, protocol,
///         credential references, a new secret and default selections, plus the model catalog drafts
///         below. Saving submits the whole card at once; failures retain every draft.
///     </para>
/// </summary>
public sealed class CatalogProviderEditorViewModel : ObservableObject
{
    private readonly string _path;
    private readonly ICredentialStore _credentials;
    private readonly UserConfig _baseline;
    private readonly UserProfile? _source;
    private readonly Action<CatalogProviderEditorViewModel> _saved;
    private readonly Action<CatalogProviderEditorViewModel> _cancelled;

    private string _name = string.Empty;
    private string _endpoint = string.Empty;
    private string _protocol = ChatApiKindParser.ChatCompletionsValue;
    private string _environmentVariable = string.Empty;
    private string _credentialTarget = string.Empty;
    private string _secret = string.Empty;
    private bool _makeDefaultProvider;
    private string _defaultModelId = string.Empty;
    private bool _isSaving;
    private string? _saveError;
    private bool _isProtocolMenuOpen;
    private bool _isDefaultModelMenuOpen;
    private IReadOnlyList<CatalogChoiceOptionViewModel> _defaultModelOptions = [];

    private CatalogProviderEditorViewModel(string path, ICredentialStore credentials, UserConfig baseline,
                                           UserProfile? source, Action<CatalogProviderEditorViewModel> saved,
                                           Action<CatalogProviderEditorViewModel> cancelled)
    {
        _path        = path;
        _credentials = credentials;
        _baseline    = baseline;
        _source      = source;
        _saved       = saved;
        _cancelled   = cancelled;
        if (source is { } provider)
        {
            _name                = provider.Name;
            _endpoint            = provider.Endpoint;
            _protocol            = ChatApiKindParser.ToValueString(provider.ChatApi);
            _environmentVariable = provider.ApiKeyEnvironmentVariable;
            _credentialTarget    = provider.ApiKeyCredentialTarget;
            _defaultModelId      = provider.DefaultModel;
            _makeDefaultProvider = baseline.DefaultProfile == provider.Name;
            foreach (var model in provider.Models)
            {
                var entry = new CatalogModelEntryViewModel(model, NotifyFormChanged);
                AttachEntry(entry);
                ModelEntries.Add(entry);
            }
        }

        ProtocolOptions =
        [
            new(ChatApiKindParser.ChatCompletionsValue, "Chat Completions",
                _protocol == ChatApiKindParser.ChatCompletionsValue, SelectProtocol),
            new(ChatApiKindParser.ResponsesValue, "Responses", _protocol == ChatApiKindParser.ResponsesValue,
                SelectProtocol)
        ];
        SaveCommand     = new AsyncCommand(SaveAsync, exception => SaveError = Redact(exception.Message));
        CancelCommand   = new RelayCommand(() => _cancelled(this));
        AddModelCommand = new RelayCommand(AddModel);
        ToggleProtocolMenuCommand =
            new RelayCommand(() => IsProtocolMenuOpen = !IsProtocolMenuOpen);
        ToggleDefaultModelMenuCommand =
            new RelayCommand(() => IsDefaultModelMenuOpen = !IsDefaultModelMenuOpen);
        RefreshDefaultModelOptions();
    }

    public static CatalogProviderEditorViewModel ForCreate(string path, ICredentialStore credentials,
                                                           UserConfig baseline,
                                                           Action<CatalogProviderEditorViewModel> saved,
                                                           Action<CatalogProviderEditorViewModel> cancelled) =>
        new(path, credentials, baseline, null, saved, cancelled);

    public static CatalogProviderEditorViewModel ForEdit(string path, ICredentialStore credentials,
                                                         UserConfig baseline, UserProfile source,
                                                         Action<CatalogProviderEditorViewModel> saved,
                                                         Action<CatalogProviderEditorViewModel> cancelled) =>
        new(path, credentials, baseline, source, saved, cancelled);

    public bool IsCreate => _source is null;

    public ObservableCollection<CatalogModelEntryViewModel> ModelEntries { get; } = [];

    public bool HasModelEntries => ModelEntries.Count > 0;

    public IReadOnlyList<CatalogChoiceOptionViewModel> ProtocolOptions { get; }

    public IReadOnlyList<CatalogChoiceOptionViewModel> DefaultModelOptions => _defaultModelOptions;

    public AsyncCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand AddModelCommand { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value)) NotifyFormChanged();
        }
    }

    public string Endpoint
    {
        get => _endpoint;
        set
        {
            if (SetProperty(ref _endpoint, value))
            {
                OnPropertyChanged(nameof(RequestPreview));
                NotifyFormChanged();
            }
        }
    }

    public string Protocol
    {
        get => _protocol;
        private set => SetProperty(ref _protocol, value);
    }

    /// <summary>
    ///     <para>协议按钮显示名；弹层选项与保存值共用同一映射，按钮呈现友好名。</para>
    ///     <para>The protocol button uses a friendly label mapped to the same values as its options and saved field.</para>
    /// </summary>
    public string ProtocolLabel =>
        _protocol == ChatApiKindParser.ResponsesValue ? "Responses" : "Chat Completions";

    public bool IsProtocolMenuOpen
    {
        get => _isProtocolMenuOpen;
        private set => SetProperty(ref _isProtocolMenuOpen, value);
    }

    public RelayCommand ToggleProtocolMenuCommand { get; }

    public string EnvironmentVariable
    {
        get => _environmentVariable;
        set
        {
            if (SetProperty(ref _environmentVariable, value))
            {
                OnPropertyChanged(nameof(SecretWatermark));
                NotifyFormChanged();
            }
        }
    }

    public string CredentialTarget
    {
        get => _credentialTarget;
        set
        {
            if (SetProperty(ref _credentialTarget, value))
            {
                OnPropertyChanged(nameof(SecretWatermark));
                NotifyFormChanged();
            }
        }
    }

    /// <summary>
    ///     <para>新 API 密钥；永不回显，保存成功后清空，失败时保留以便重试。</para>
    ///     <para>The new API key; never displayed, cleared on success and retained on failure for retries.</para>
    /// </summary>
    public string Secret
    {
        get => _secret;
        set
        {
            if (SetProperty(ref _secret, value)) NotifyFormChanged();
        }
    }

    public bool MakeDefaultProvider
    {
        get => _makeDefaultProvider;
        set
        {
            if (SetProperty(ref _makeDefaultProvider, value)) NotifyFormChanged();
        }
    }

    public string DefaultModelLabel => _defaultModelId.Length == 0 ? "无默认" : _defaultModelId;

    public bool IsDefaultModelMenuOpen
    {
        get => _isDefaultModelMenuOpen;
        private set => SetProperty(ref _isDefaultModelMenuOpen, value);
    }

    public RelayCommand ToggleDefaultModelMenuCommand { get; }

    public string RequestPreview => $"POST {Endpoint.Trim().TrimEnd('/')}" +
                                    (_protocol == ChatApiKindParser.ResponsesValue
                                        ? "/responses"
                                        : "/chat/completions");

    /// <summary>
    ///     <para>密钥框占位：已有引用提示替换语义，否则提示可选输入；值不回显。</para>
    ///     <para>The secret placeholder explains replacement when references exist; otherwise input is optional and never revealed.</para>
    /// </summary>
    public string SecretWatermark =>
        _source is { } provider && (provider.ApiKeyEnvironmentVariable.Length > 0 ||
                                    provider.ApiKeyCredentialTarget.Length    > 0)
            ? "已保存凭据引用——输入新密钥将替换"
            : "新 API 密钥（可选）";

    public bool CanEdit => !_isSaving;

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (!SetProperty(ref _isSaving, value)) return;
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(SaveButtonText));
        }
    }

    public string? SaveError
    {
        get => _saveError;
        private set
        {
            if (SetProperty(ref _saveError, value)) OnPropertyChanged(nameof(HasSaveError));
        }
    }

    public bool HasSaveError => !string.IsNullOrEmpty(SaveError);

    public string SaveButtonText => _isSaving ? "保存中…" : "保存";

    /// <summary>
    ///     <para>校验错误（首个）；字段级规则在此预检，命名与 URL 细则由 Core 校验在保存时报出。</para>
    ///     <para>The first validation error; fields are checked here and Core validates names and URLs on save.</para>
    /// </summary>
    public string? FirstValidationError
    {
        get
        {
            if (IsCreate && Name.Trim().Length == 0) return "请填写供应商名称。";
            if (Endpoint.Trim().Length == 0) return "请填写接口地址。";
            if (Secret.Length == 0 && EnvironmentVariable.Trim().Length > 0 && CredentialTarget.Trim().Length > 0)
                return "凭据引用请只填写环境变量或系统凭据目标中的一种。";
            var identifiers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in ModelEntries)
            {
                var id = entry.Id.Trim();
                if (id.Length == 0) return "模型 ID 不能为空。";
                if (id.Any(char.IsWhiteSpace)) return "模型 ID 不能包含空白字符。";
                if (!identifiers.Add(id)) return $"模型 ID 重复：{id}";
                if (!CatalogModelEntryViewModel.TryParseWindow(entry.WindowDraft, out _))
                    return "上下文窗口必须是正整数（可带 K/M 后缀）。";
                if (entry.InvalidDefaultEffort) return $"默认档位 {entry.DefaultEffortValue} 不在已勾选的支持档位中。";
            }

            return null;
        }
    }

    public bool HasValidationError => FirstValidationError is not null;

    public bool IsDirty
    {
        get
        {
            if (Secret.Length > 0) return true;
            if (IsCreate)
                return Name.Trim().Length       > 0 || Endpoint.Trim().Length > 0 || _environmentVariable.Length > 0 ||
                       _credentialTarget.Length > 0 || _makeDefaultProvider       || _defaultModelId.Length      > 0 ||
                       ModelEntries.Count       > 0;
            var provider = _source!;
            if (provider.Endpoint                                 != Endpoint.Trim().TrimEnd('/')) return true;
            if (ChatApiKindParser.ToValueString(provider.ChatApi) != _protocol) return true;
            if (provider.ApiKeyEnvironmentVariable                != EnvironmentVariable.Trim()) return true;
            if (provider.ApiKeyCredentialTarget                   != CredentialTarget.Trim()) return true;
            if (_makeDefaultProvider && _baseline.DefaultProfile != provider.Name) return true;
            if (_defaultModelId != provider.DefaultModel) return true;
            return ModelEntries.Any(entry => entry.IsDraft || !entry.MatchesSource());
        }
    }

    public bool CanSave => CanEdit && IsDirty && !HasValidationError;

    /// <summary>
    ///     <para>保存整卡：供应商字段、全部模型条目与默认项一次提交；冲突或校验失败保留草稿。</para>
    ///     <para>Saves the whole card: provider fields, every model entry and defaults in one commit; conflicts or validation failures retain drafts.</para>
    /// </summary>
    public async Task SaveAsync()
    {
        if (!CanSave) return;

        IsSaving  = true;
        SaveError = null;
        try
        {
            var name = Name.Trim();
            if (!ChatApiKindParser.TryParse(_protocol, out var kind))
                throw new InvalidDataException("请选择协议。");
            var provider = new UserProfile
            {
                Name                      = name,
                Endpoint                  = Endpoint.Trim().TrimEnd('/'),
                ChatApi                   = kind,
                ApiKeyEnvironmentVariable = EnvironmentVariable.Trim(),
                ApiKeyCredentialTarget    = CredentialTarget.Trim()
            };
            var candidate = ModelCatalogEditor.PutProvider(_baseline, provider, !IsCreate);
            foreach (var entry in ModelEntries)
            {
                if (!entry.TryGetModel(out var model))
                    throw new InvalidDataException("模型目录存在未填完的条目。");
                candidate = ModelCatalogEditor.PutModel(candidate, name, model, !entry.IsDraft);
            }

            candidate = candidate with
            {
                Profiles = candidate.Profiles
                                    .Select(item => item.Name == name
                                                ? item with { DefaultModel = _defaultModelId }
                                                : item)
                                    .ToArray()
            };
            if (_makeDefaultProvider) candidate = candidate with { DefaultProfile = name };
            var secret                          = _secret;
            await ModelCatalogEditor.SaveAsync(_path, _baseline, candidate, CancellationToken.None, _credentials,
                                               secret, name);
            _secret = string.Empty;
            OnPropertyChanged(nameof(Secret));
            OnPropertyChanged(nameof(SecretWatermark));
            _saved(this);
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void AddModel()
    {
        var entry = new CatalogModelEntryViewModel(null, NotifyFormChanged);
        entry.IsExpanded = true;
        AttachEntry(entry);
        ModelEntries.Add(entry);
        SaveError = null;
        RefreshDefaultModelOptions();
        NotifyFormChanged();
        OnPropertyChanged(nameof(HasModelEntries));
    }

    private void AttachEntry(CatalogModelEntryViewModel entry) =>
        entry.PropertyChanged += OnEntryPropertyChanged;

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        SaveError = null;
        if (e.PropertyName == nameof(CatalogModelEntryViewModel.Id)) RefreshDefaultModelOptions();
        NotifyFormChanged();
    }

    private void SelectProtocol(string value)
    {
        IsProtocolMenuOpen = false;
        if (_protocol == value) return;

        _protocol = value;
        OnPropertyChanged(nameof(Protocol));
        OnPropertyChanged(nameof(ProtocolLabel));
        OnPropertyChanged(nameof(RequestPreview));
        foreach (var option in ProtocolOptions) option.Refresh(option.Value == value);
        NotifyFormChanged();
    }

    private void SelectDefaultModel(string value)
    {
        IsDefaultModelMenuOpen = false;
        if (_defaultModelId == value) return;

        _defaultModelId = value;
        OnPropertyChanged(nameof(DefaultModelLabel));
        foreach (var option in DefaultModelOptions) option.Refresh(option.Value == value);
        NotifyFormChanged();
    }

    private void RefreshDefaultModelOptions()
    {
        var options = new List<CatalogChoiceOptionViewModel>
        {
            new(string.Empty, "无默认", _defaultModelId.Length == 0, SelectDefaultModel)
        };
        foreach (var entry in ModelEntries)
        {
            var id = entry.Id.Trim();
            if (id.Length == 0) continue;
            options.Add(new CatalogChoiceOptionViewModel(id, id, _defaultModelId == id, SelectDefaultModel));
        }

        _defaultModelOptions = options;
        OnPropertyChanged(nameof(DefaultModelOptions));
        OnPropertyChanged(nameof(DefaultModelLabel));
    }

    private void NotifyFormChanged()
    {
        SaveError = null;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(FirstValidationError));
        OnPropertyChanged(nameof(HasValidationError));
    }

    /// <summary>
    ///     <para>异常正文不直接回显前先抹去本卡输入的密钥，防止错误消息携带秘密。</para>
    ///     <para>Removes this card's secret from exception text before displaying it.</para>
    /// </summary>
    private string Redact(string message) =>
        _secret.Length > 0 ? message.Replace(_secret, "[REDACTED]", StringComparison.Ordinal) : message;
}

/// <summary>
///     <para>
///         模型目录的单条草稿：折叠仅显示 ID 与显示名，展开补上下文窗口、支持的推理档位
///         与默认档位。API ID 是保存键：已有条目只读，替换 ID 走另行添加。
///     </para>
///     <para>
///         One catalog draft entry: collapsed to ID and display name, expanded for the context window,
///         supported reasoning efforts and the default effort. The API ID is the save key: existing
///         entries are read-only, and replacing an ID means adding another entry.
///     </para>
/// </summary>
public sealed class CatalogModelEntryViewModel : ObservableObject
{
    /// <summary>
    ///     <para>默认档位保留字：不向服务发送强度参数，由服务决定。</para>
    ///     <para>The default-effort token omits the request parameter and lets the service decide.</para>
    /// </summary>
    public const string NoEffortValue = "default";

    private static readonly ModelReasoningEffort[] EffortOrder =
    [
        ModelReasoningEffort.None, ModelReasoningEffort.Low, ModelReasoningEffort.Medium, ModelReasoningEffort.High,
        ModelReasoningEffort.ExtraHigh
    ];

    private readonly Action _onChanged;
    private string _id;
    private string _displayName;
    private string _windowDraft;
    private bool _isExpanded;
    private bool _isEffortMenuOpen;
    private bool _isNoneSelected;
    private bool _isLowSelected;
    private bool _isMediumSelected;
    private bool _isHighSelected;
    private bool _isExtraHighSelected;
    private string _defaultEffortValue;
    private IReadOnlyList<CatalogChoiceOptionViewModel> _defaultEffortOptions = [];

    public CatalogModelEntryViewModel(UserProfileModel? source, Action onChanged)
    {
        Source       = source;
        IsDraft      = source is null;
        _onChanged   = onChanged;
        _id          = source?.Id          ?? string.Empty;
        _displayName = source?.DisplayName ?? string.Empty;
        _windowDraft = source is null ? string.Empty : FormatTokenCount(source.ContextWindowTokens);
        if (source?.ReasoningEfforts is { } efforts)
            foreach (var effort in efforts)
                SetEffortTick(effort, true);
        _defaultEffortValue = source?.DefaultReasoningEffort is { } level
            ? ModelReasoningEffortParser.ToValueString(level)
            : NoEffortValue;
        ToggleExpandCommand     = new RelayCommand(() => IsExpanded       = !IsExpanded);
        ToggleEffortMenuCommand = new RelayCommand(() => IsEffortMenuOpen = !IsEffortMenuOpen);
        RebuildDefaultEffortOptions();
    }

    /// <summary>
    ///     <para>打开时的原始模型；新建条目为 null。保存时已有条目按原 ID 覆盖。</para>
    ///     <para>The original model, or null for a new entry; existing entries are saved under their original IDs.</para>
    /// </summary>
    public UserProfileModel? Source { get; }

    public bool IsDraft { get; }

    public bool IsIdEnabled => IsDraft;

    public string Id
    {
        get => _id;
        set
        {
            if (SetProperty(ref _id, value)) _onChanged();
        }
    }

    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (SetProperty(ref _displayName, value)) _onChanged();
        }
    }

    public string WindowDraft
    {
        get => _windowDraft;
        set
        {
            if (SetProperty(ref _windowDraft, value)) _onChanged();
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsEffortMenuOpen
    {
        get => _isEffortMenuOpen;
        private set => SetProperty(ref _isEffortMenuOpen, value);
    }

    public RelayCommand ToggleExpandCommand { get; }

    public RelayCommand ToggleEffortMenuCommand { get; }

    public bool IsNoneSelected
    {
        get => _isNoneSelected;
        set
        {
            if (SetProperty(ref _isNoneSelected, value)) OnEffortsChanged();
        }
    }

    public bool IsLowSelected
    {
        get => _isLowSelected;
        set
        {
            if (SetProperty(ref _isLowSelected, value)) OnEffortsChanged();
        }
    }

    public bool IsMediumSelected
    {
        get => _isMediumSelected;
        set
        {
            if (SetProperty(ref _isMediumSelected, value)) OnEffortsChanged();
        }
    }

    public bool IsHighSelected
    {
        get => _isHighSelected;
        set
        {
            if (SetProperty(ref _isHighSelected, value)) OnEffortsChanged();
        }
    }

    public bool IsExtraHighSelected
    {
        get => _isExtraHighSelected;
        set
        {
            if (SetProperty(ref _isExtraHighSelected, value)) OnEffortsChanged();
        }
    }

    public string DefaultEffortValue => _defaultEffortValue;

    public string DefaultEffortLabel => _defaultEffortValue == NoEffortValue ? "默认（由服务决定）" : _defaultEffortValue;

    public IReadOnlyList<CatalogChoiceOptionViewModel> DefaultEffortOptions => _defaultEffortOptions;

    /// <summary>
    ///     <para>默认档位必须属于已勾选的支持档位；选择“默认”始终合法。</para>
    ///     <para>The default effort must be among the declared levels; the service default is always valid.</para>
    /// </summary>
    public bool InvalidDefaultEffort =>
        _defaultEffortValue != NoEffortValue && !IsEffortTicked(_defaultEffortValue);

    public bool TryGetModel(out UserProfileModel model)
    {
        model = new UserProfileModel();
        if (!TryParseWindow(_windowDraft, out var window)) return false;
        ModelReasoningEffort? defaultEffort = null;
        if (_defaultEffortValue != NoEffortValue)
        {
            if (!ModelReasoningEffortParser.TryParse(_defaultEffortValue, out var parsed)) return false;
            defaultEffort = parsed;
        }

        var efforts = EffortOrder.Where(effort => IsEffortTicked(ModelReasoningEffortParser.ToValueString(effort)))
                                 .ToArray();
        model = new UserProfileModel
        {
            Id                     = _id.Trim(),
            DisplayName            = string.IsNullOrWhiteSpace(_displayName) ? null : _displayName.Trim(),
            ContextWindowTokens    = window,
            ReasoningEfforts       = efforts.Length == 0 ? null : efforts,
            DefaultReasoningEffort = defaultEffort
        };
        return true;
    }

    /// <summary>
    ///     <para>草稿是否与打开时一致；解析失败按已修改处理，保存按钮由校验兜底。</para>
    ///     <para>Whether the draft matches its original value; invalid input counts as changed and validation gates saving.</para>
    /// </summary>
    public bool MatchesSource()
    {
        if (Source is not { } source) return false;
        if (!TryGetModel(out var model)) return false;
        if (model.Id                            != source.Id) return false;
        if ((model.DisplayName ?? string.Empty) != (source.DisplayName ?? string.Empty)) return false;
        if (model.ContextWindowTokens           != source.ContextWindowTokens) return false;
        if (model.ReasoningEfforts is { } efforts)
        {
            if (source.ReasoningEfforts is not { } sourceEfforts || efforts.Count != sourceEfforts.Count) return false;
            if (efforts.Any(effort => !sourceEfforts.Contains(effort))) return false;
        }
        else if (source.ReasoningEfforts is { Count: > 0 })
        {
            return false;
        }

        return model.DefaultReasoningEffort == source.DefaultReasoningEffort;
    }

    private bool IsEffortTicked(string value) => value switch
    {
        ModelReasoningEffortParser.NoneValue      => _isNoneSelected,
        ModelReasoningEffortParser.LowValue       => _isLowSelected,
        ModelReasoningEffortParser.MediumValue    => _isMediumSelected,
        ModelReasoningEffortParser.HighValue      => _isHighSelected,
        ModelReasoningEffortParser.ExtraHighValue => _isExtraHighSelected,
        _                                         => false
    };

    private void SetEffortTick(ModelReasoningEffort effort, bool value)
    {
        switch (effort)
        {
            case ModelReasoningEffort.None :      _isNoneSelected      = value; break;
            case ModelReasoningEffort.Low :       _isLowSelected       = value; break;
            case ModelReasoningEffort.Medium :    _isMediumSelected    = value; break;
            case ModelReasoningEffort.High :      _isHighSelected      = value; break;
            case ModelReasoningEffort.ExtraHigh : _isExtraHighSelected = value; break;
        }
    }

    private void OnEffortsChanged()
    {
        RebuildDefaultEffortOptions();
        _onChanged();
    }

    private void SelectDefaultEffort(string value)
    {
        IsEffortMenuOpen = false;
        if (_defaultEffortValue == value) return;

        _defaultEffortValue = value;
        OnPropertyChanged(nameof(DefaultEffortValue));
        OnPropertyChanged(nameof(DefaultEffortLabel));
        foreach (var option in _defaultEffortOptions) option.Refresh(option.Value == value);
        _onChanged();
    }

    private void RebuildDefaultEffortOptions()
    {
        var options = new List<CatalogChoiceOptionViewModel>
        {
            new(NoEffortValue, "默认（由服务决定）", _defaultEffortValue == NoEffortValue, SelectDefaultEffort)
        };
        foreach (var effort in EffortOrder)
        {
            var value = ModelReasoningEffortParser.ToValueString(effort);
            if (IsEffortTicked(value)) options.Add(new CatalogChoiceOptionViewModel(value, value,
                                                                                    _defaultEffortValue == value,
                                                                                    SelectDefaultEffort));
        }

        _defaultEffortOptions = options;
        OnPropertyChanged(nameof(DefaultEffortOptions));
    }

    /// <summary>
    ///     <para>大数缩写显示：整百万/整千缩为 M/K（1000000→1M、128000→128K），其余原样。</para>
    ///     <para>Formats exact millions and thousands with M or K suffixes and leaves other counts unchanged.</para>
    /// </summary>
    internal static string FormatTokenCount(long value)
    {
        if (value             == 0) return "0";
        if (value % 1_000_000 == 0) return (value / 1_000_000).ToString(CultureInfo.InvariantCulture) + "M";
        if (value % 1_000     == 0) return (value / 1_000).ToString(CultureInfo.InvariantCulture)     + "K";
        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     <para>解析窗口草稿：正整数（可带 K/M 后缀，1M=1000000），不得超过 int 上限。</para>
    ///     <para>Parses a positive window with optional K or M suffixes, capped at the maximum int value.</para>
    /// </summary>
    public static bool TryParseWindow(string text, out int tokens)
    {
        tokens = 0;
        if (!TryParseTokenCount(text, out var value)) return false;
        if (value <= 0 || value > int.MaxValue) return false;
        tokens = (int)value;
        return true;
    }

    private static bool TryParseTokenCount(string text, out long value)
    {
        value = 0;
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return false;

        long multiplier = 1;
        switch (trimmed[^1])
        {
            case 'K' or 'k' :
                multiplier = 1_000;
                trimmed    = trimmed[..^1];
                break;
            case 'M' or 'm' :
                multiplier = 1_000_000;
                trimmed    = trimmed[..^1];
                break;
        }

        if (!long.TryParse(trimmed, out var number) || number < 0) return false;
        if (multiplier                                        > 1 && number > long.MaxValue / multiplier) return false;
        value = number * multiplier;
        return true;
    }
}

/// <summary>
///     <para>下拉弹层的单个选项：值、显示标签与选中态；选择动作由宿主字段的回调承担。</para>
///     <para>One dropdown option: value, label and selection state; the select action goes to the owning field's callback.</para>
/// </summary>
public sealed class CatalogChoiceOptionViewModel : ObservableObject
{
    private bool _isSelected;

    public CatalogChoiceOptionViewModel(string value, string label, bool isSelected, Action<string> onSelected)
    {
        Value         = value;
        Label         = label;
        _isSelected   = isSelected;
        SelectCommand = new RelayCommand(() => onSelected(Value));
    }

    public string Value { get; }

    public string Label { get; }

    public bool IsSelected
    {
        get => _isSelected;
        private set => SetProperty(ref _isSelected, value);
    }

    public RelayCommand SelectCommand { get; }

    internal void Refresh(bool isSelected) => IsSelected = isSelected;
}
