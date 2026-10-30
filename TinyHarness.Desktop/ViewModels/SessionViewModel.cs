using Avalonia.Threading;
using System.Collections.ObjectModel;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Desktop.Models;
using TinyHarness.Desktop.Runtime;

namespace TinyHarness.Desktop.ViewModels;

/// <summary>
///     <para>保存一个会话的草稿和显示投影；所有回调绑定该实例，不依赖当前选中页面。</para>
///     <para>Holds one session's draft and display projection; callbacks bind this instance rather than the selected page.</para>
/// </summary>
public sealed partial class SessionViewModel : ObservableObject
{
    private readonly Func<Action, ValueTask>                       _dispatch;
    private readonly DesktopSessionFactory                         _factory;
    private readonly HashSet<string>                               _historyIds = new(StringComparer.Ordinal);
    private readonly DesktopSessionManager                         _manager;
    private readonly Dictionary<string, ProcessGroupItemViewModel> _processGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string>                    _runLabels     = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolActivityItemViewModel> _tools         = new(StringComparer.Ordinal);
    private          string?                                       _activeRunId;
    private          bool                                          _allowChanges;
    private          DesktopApproval?                              _approval;
    private          bool                                          _changing;
    private          Task?                                         _completion;
    private          string                                        _draft = string.Empty;
    private          long                                          _draftRevision;
    private          ModelReasoningEffort?                         _effort;
    private          bool                                          _established;
    private          SessionModelIdentity?                         _historicalIdentity;
    private          SessionHistoryCursor?                         _historyCursor;
    private          Task?                                         _historyLoad;
    private          bool                                          _historyLoaded;
    private          ModelChoice?                                  _model;
    private          string                                        _notice = string.Empty;
    private          bool                                          _opening;
    private          CancellationTokenSource?                      _openingCancellation;
    private          string?                                       _runError;
    private          bool                                          _running;
    private          DesktopSession?                               _runtime;
    private          SessionModelSelection?                        _savedSelection;
    private          bool                                          _selected;
    private          string                                        _status = "就绪";
    private          MessageItemViewModel?                         _stream;
    private          long?                                         _submittedDraftRevision;
    private          string                                        _title;
    private          string                                        _workspaceRoot;

    public SessionViewModel(
        DesktopSessionManager    manager,         DesktopSessionFactory factory,               string  workspaceRoot,
        string?                  id       = null, string?               storeDirectory = null, string? title = null,
        Func<Action, ValueTask>? dispatch = null)
        : this(manager, factory, workspaceRoot, id, storeDirectory, title, dispatch, null, null)
    {
    }

    public SessionViewModel(
        DesktopSessionManager manager,   DesktopSessionFactory factory, string workspaceRoot,
        string?               id,        string? storeDirectory, string? title, Func<Action, ValueTask>? dispatch,
        string?               projectId, DateTimeOffset? lastActivityUtc)
    {
        _manager           = manager;
        _factory           = factory;
        _dispatch          = dispatch ?? (async action => await Dispatcher.UIThread.InvokeAsync(action));
        _workspaceRoot     = workspaceRoot;
        Id                 = id;
        StoreDirectory     = storeDirectory;
        ProjectId          = projectId;
        LastActivityUtc    = lastActivityUtc ?? DateTimeOffset.UtcNow;
        _title             = title           ?? "新对话";
        SendCommand        = new AsyncCommand(SendAsync, ShowError);
        LoadHistoryCommand = new AsyncCommand(LoadHistoryAsync, ShowError);
        CancelCommand = new RelayCommand(() =>
        {
            _openingCancellation?.Cancel();
            if (_runtime?.Cancel() == true) Status = "取消中，等待清理和保存";
        });
        PauseCommand = new RelayCommand(() =>
        {
            if (_runtime?.Host.RequestPause() == true) Status = "已请求暂停";
        });
        ResumeCommand    = new RelayCommand(() => _runtime?.Host.ResumeRun());
        AllowOnceCommand = new RelayCommand(() => Respond(ApprovalAction.AllowOnce));
        AllowRunCommand  = new RelayCommand(() => Respond(ApprovalAction.AllowSession));
        DenyCommand      = new RelayCommand(() => Respond(ApprovalAction.Deny));
        AcknowledgeInspectionCommand = new AsyncCommand(async () =>
        {
            if (_runtime is null || IsRunning) return;
            await _runtime.Host.AcknowledgeInspectionAsync(CancellationToken.None);
            UpdateNotices();
        }, ShowError);
        InitializeComposer();
        InitializeWorkspaceRepair();
    }

    public string? Id { get; private set; }

    public bool IsSelected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }

    public string?        StoreDirectory  { get; private set; }
    public string?        ProjectId       { get; private set; }
    public DateTimeOffset LastActivityUtc { get; private set; }

    /// <summary>
    ///     <para>用户是否在页面未发送前做过显式预选（项目／目录、模型或权限）；自动解析与默认刷新不算。</para>
    ///     <para>
    ///         Whether the user made an explicit preselection (project/directory, model or permission) before sending;
    ///         automatic resolution and default refresh never count.
    ///     </para>
    /// </summary>
    public bool HasUserPreselection { get; private set; }

    /// <summary>
    ///     <para>页面目标选择的当前代际；目标意图登记时递增。</para>
    ///     <para>The page's target-intent revision, advanced whenever a target intent is registered.</para>
    /// </summary>
    internal int TargetIntentRevision { get; private set; }

    /// <summary>
    ///     <para>当前显式目标解析是否在途；自动绑定不得抢占。</para>
    ///     <para>Whether an explicit target resolution is pending; automatic binding must not outrank it.</para>
    /// </summary>
    internal bool TargetIntentPending { get; private set; }

    public ObservableCollection<ConversationItemViewModel> Items { get; } = [];
    public ObservableCollection<string>                    WorkItems { get; } = [];
    public Task                                            Completion => _completion ?? Task.CompletedTask;
    public IReadOnlyList<ModelChoice>                      ModelOptions { get; init; } = [];
    public AsyncCommand                                    SendCommand { get; }
    public AsyncCommand                                    LoadHistoryCommand { get; }
    public RelayCommand                                    CancelCommand { get; }
    public RelayCommand                                    PauseCommand { get; }
    public RelayCommand                                    ResumeCommand { get; }
    public RelayCommand                                    AllowOnceCommand { get; }
    public RelayCommand                                    AllowRunCommand { get; }
    public RelayCommand                                    DenyCommand { get; }
    public AsyncCommand                                    AcknowledgeInspectionCommand { get; }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string Notice
    {
        get => _notice;
        private set
        {
            if (SetProperty(ref _notice, value)) OnPropertyChanged(nameof(HasNotice));
        }
    }

    public bool HasNotice => Notice.Length > 0;

    public string WorkspaceRoot
    {
        get => _workspaceRoot;
        set
        {
            if (string.Equals(_workspaceRoot, value, StringComparison.Ordinal)) return;
            if (!CanChooseWorkspace) throw new InvalidOperationException("会话根目录已固定，请新建对话选择其他目录。");
            CaptureTargetIntent();
            TargetIntentPending = false;
            HasUserPreselection = true;
            if (SetProperty(ref _workspaceRoot, value))
            {
                ProjectId      = null;
                StoreDirectory = null;
                OnPropertyChanged(nameof(ProjectId));
                OnPropertyChanged(nameof(WorkspaceLabel));
                OnPropertyChanged(nameof(WorkspaceTargetLabel));
                _ = RefreshDefaultModelAsync();
            }
        }
    }

    public bool AllowChanges
    {
        get => _allowChanges;
        set
        {
            if (_allowChanges == value) return;
            if (!CanConfigure) throw new InvalidOperationException("当前会话的权限设置已经生效。");
            if (SetProperty(ref _allowChanges, value))
            {
                // 权限预选是用户显式选择，参与空白页回收判定。
                HasUserPreselection = true;
                OnPropertyChanged(nameof(PermissionPickerLabel));
            }
        }
    }

    public ModelChoice? SelectedModel
    {
        get => _model;
        set
        {
            if (Equals(_model, value)) return;
            if (Id is not null || !CanChangeModel) throw new InvalidOperationException("已建立会话请通过异步选择入口切换模型。");
            if (SetProperty(ref _model, value))
            {
                _effort = value?.DefaultEffort;
                NotifyModelSelection();
            }
        }
    }

    public string Draft
    {
        get => _draft;
        set
        {
            if (SetProperty(ref _draft, value)) _draftRevision++;
        }
    }

    public bool CanConfigure       => _runtime is null && !_opening && !_changing;
    public bool CanChooseWorkspace => Id is null       && CanConfigure;

    public bool IsRunning
    {
        get => _running;
        private set
        {
            if (!SetProperty(ref _running, value)) return;
            OnPropertyChanged(nameof(CanChangeModel));
            OnPropertyChanged(nameof(ComposerSendHint));
            OnPropertyChanged(nameof(CanRepairWorkspace));
            OpenWorkspaceRepairCommand.RaiseCanExecuteChanged();
        }
    }

    public string ComposerSendHint => IsRunning ? "Enter 发送补充 · Shift+Enter 换行" : "Enter 发送 · Shift+Enter 换行";

    public bool HasMoreHistory    => _historyCursor is not null;
    public bool InspectionPending => _runtime?.Host.InspectionPending == true;
    public bool HasApproval       => _approval is not null;

    public string ApprovalText => _approval is { } approval
        ? $"{approval.ToolName}\n{approval.Summary}\n{approval.Arguments}\n{string.Join('\n', approval.TargetPaths)}\n{approval.ExecutionPolicy}"
        : string.Empty;

    /// <summary>
    ///     <para>首次取得真实 SessionId 与宿主（首发进入发送流程）时触发一次；主窗口据此把页面登记为正式会话行。</para>
    ///     <para>
    ///         Fires once when the page first gains a real SessionId and host (the first send enters its flow); the main
    ///         window registers the formal row here.
    ///     </para>
    /// </summary>
    public event Action<SessionViewModel>? Established;

    /// <summary>
    ///     <para>首发在取得真实 SessionId 前失败时触发；页面保持草稿并保留文字与预选，主窗口决定是否回收或保留恢复入口。</para>
    ///     <para>
    ///         Fires when the first send fails before a real SessionId exists; the page stays a draft with its text and
    ///         preselections, and the main window decides reclamation or recovery.
    ///     </para>
    /// </summary>
    public event Action<SessionViewModel>? FirstSendFailed;

    /// <summary>
    ///     <para>登记一次新的目标意图并返回其代际；意图的异步解析在应用前须确认代际未前进，过时解析不得改归属。</para>
    ///     <para>
    ///         Registers a new target intent and returns its revision; the intent's async resolution must confirm the
    ///         revision has not advanced before applying, so stale resolutions never rewrite ownership.
    ///     </para>
    /// </summary>
    internal int CaptureTargetIntent() => ++TargetIntentRevision;

    /// <summary>
    ///     <para>开始显式目标选择并保留用户预选事实；返回用于异步结果校验的代际。</para>
    ///     <para>Begins an explicit target choice and retains the user-preselection fact, returning its async-result revision.</para>
    /// </summary>
    internal int BeginTargetIntent()
    {
        TargetIntentPending = true;
        HasUserPreselection = true;
        return CaptureTargetIntent();
    }

    /// <summary>
    ///     <para>仅结束同一代显式解析；旧操作的 finally 不清除更新意图的在途状态。</para>
    ///     <para>Ends only the same explicit resolution; an older finally never clears a newer pending intent.</para>
    /// </summary>
    internal void CompleteTargetIntent(int revision)
    {
        if (revision == TargetIntentRevision) TargetIntentPending = false;
    }

    /// <summary>
    ///     <para>把未发送草稿切到指定绑定；项目为空时仅绑定当前存储目录，归属留待首次发送解析。正式会话与正在打开的草稿拒绝切换。</para>
    ///     <para>
    ///         Moves an unsent draft to the given binding; a null project binds only the store directory and leaves
    ///         ownership to the first send. Established or opening sessions reject changes.
    ///     </para>
    /// </summary>
    public async Task SelectProjectAsync(string? projectId, string root, string directory, bool userSelected = true)
    {
        if (!CanChooseWorkspace) throw new InvalidOperationException("会话执行目录已固定。");
        _workspaceRoot = root;
        ProjectId      = projectId;
        StoreDirectory = directory;
        if (userSelected) HasUserPreselection = true;
        OnPropertyChanged(nameof(WorkspaceRoot));
        OnPropertyChanged(nameof(WorkspaceLabel));
        OnPropertyChanged(nameof(ProjectId));
        OnPropertyChanged(nameof(WorkspaceTargetLabel));
        await RefreshDefaultModelAsync();
    }

    /// <summary>
    ///     <para>只读加载一个有界历史页；不创建宿主或取得 writer 锁。</para>
    ///     <para>Reads one bounded history page without creating a host or acquiring a writer lock.</para>
    /// </summary>
    public async Task LoadHistoryAsync()
    {
        if (Id is null || StoreDirectory is null || (_historyLoaded && _historyCursor is null)) return;
        if (!_historyLoaded)
        {
            var stored = await SessionStoreReader.ReadAsync(StoreDirectory, Id, CancellationToken.None);
            if (stored is not null)
            {
                _savedSelection = stored.ModelSelection;
                _historicalIdentity = stored.Model;
                _runError = _factory.Redact(stored.Turns.LastOrDefault()?.Runs.LastOrDefault()?.Error ?? string.Empty);
                foreach (var run in stored.Turns.SelectMany(turn => turn.Runs))
                {
                    var selection =
                        stored.ModelSelections.FirstOrDefault(item => item.Revision == run.ModelSelectionRevision);
                    _runLabels[run.Id] =
                        selection is null ? $"{stored.Model?.ModelId} · 强度未记录" : SelectionLabel(selection);
                }

                NotifyModelSelection();
            }
        }

        var page = await new SessionHistoryReader().ReadPageAsync(StoreDirectory, Id, 200, _historyCursor,
                                                                  CancellationToken.None);
        foreach (var item in page.Items) ApplyHistory(item);
        _historyCursor = page.NextCursor;
        _historyLoaded = true;
        if (!string.IsNullOrEmpty(_runError) || page.Warning is not null)
            Notice = string.Join('\n', new[] { _runError, page.Warning }.Where(value => !string.IsNullOrEmpty(value)));
        OnPropertyChanged(nameof(HasMoreHistory));
    }

    public Task EnsureHistoryLoadedAsync() => _historyLoad ??= LoadHistoryAsync();

    /// <summary>
    ///     <para>用目录刷新结果更新归属与活动时间；不替换实例、宿主、草稿或历史投影，活动只前进不回退。</para>
    ///     <para>
    ///         Applies catalog-refresh ownership and activity without replacing the instance, host, draft or history
    ///         projection; activity only moves forward.
    ///     </para>
    /// </summary>
    public void ApplyStoredMetadata(string? projectId, DateTimeOffset lastActivityUtc)
    {
        if (ProjectId != projectId)
        {
            ProjectId = projectId;
            OnPropertyChanged(nameof(ProjectId));
            OnPropertyChanged(nameof(WorkspaceTargetLabel));
        }

        // 运行中的行是本地活动权威；目录快照只允许推进排序，不允许回退。
        if (lastActivityUtc > LastActivityUtc)
        {
            LastActivityUtc = lastActivityUtc;
            OnPropertyChanged(nameof(LastActivityUtc));
        }
    }

    /// <summary>
    ///     <para>捕获本页草稿和版本后发送；运行中输入走 Core 补充入口，完成只消费原始草稿。</para>
    ///     <para>
    ///         Captures this page's draft and revision before sending; active input uses Core amendments and consumes only
    ///         that draft.
    ///     </para>
    /// </summary>
    public async Task SendAsync()
    {
        if (_changing) throw new InvalidOperationException("正在切换模型，请稍后发送。");
        var text = Draft;
        if (string.IsNullOrWhiteSpace(text)) return;
        var revision = _draftRevision;
        CloseComposerMenus();
        if (IsRunning)
        {
            if (_runtime is null) throw new InvalidOperationException("会话正在打开，请稍后发送补充。");
            await _runtime.Host.QueueAmendmentAsync(text, CancellationToken.None);
            if (_draftRevision == revision) Draft = string.Empty;
            Status = "补充已保存，等待安全边界生效";
            return;
        }

        IsRunning = true;
        try
        {
            if (_runtime is null)
            {
                _opening             = true;
                _openingCancellation = new CancellationTokenSource();
                OnPropertyChanged(nameof(CanConfigure));
                OnPropertyChanged(nameof(CanChooseWorkspace));
                // 必须先完成历史读取，避免实时条目插在未加载的旧记录之前。
                await EnsureHistoryLoadedAsync();
                while (HasMoreHistory) await LoadHistoryAsync();
                await OpenRuntimeAsync(false);
            }

            _submittedDraftRevision = revision;
            _runError               = null;
            UpdateNotices();
            _completion = CompleteRunAsync(_runtime!.SubmitAsync(text));
        }
        catch
        {
            IsRunning = false;
            // 尚无真实 ID：页面仍是草稿并保留文字与预选；通知主窗口回收空白槽或登记恢复入口。
            if (!_established) FirstSendFailed?.Invoke(this);
            throw;
        }
        finally
        {
            _opening = false;
            _openingCancellation?.Dispose();
            _openingCancellation = null;
            OnPropertyChanged(nameof(CanConfigure));
            OnPropertyChanged(nameof(CanChooseWorkspace));
            OnPropertyChanged(nameof(CanChangeModel));
        }
    }

    private async Task CompleteRunAsync(Task<SessionRun> execution)
    {
        try
        {
            var run = await execution;
            _runError       = run.Result?.Error is { } error ? _factory.Redact(error) : null;
            LastActivityUtc = DateTimeOffset.UtcNow;
            OnPropertyChanged(nameof(LastActivityUtc));
            Status = run.Result?.Status.ToString() ?? "运行结束";
            WorkItems.Clear();
            foreach (var item in _runtime!.Host.Session.WorkItems)
                WorkItems.Add($"[{item.Status}] {item.DisplayTitle}");
            UpdateNotices();
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            StopStream();
            if (_activeRunId is { } runId && _processGroups.TryGetValue(runId, out var group)) group.Complete();
            _activeRunId = null;
            IsRunning    = false;
            NotifyModelSelection();
        }
    }

    private ValueTask OnEventAsync(DesktopEvent update) => _dispatch(() => ApplyEvent(update));

    private ValueTask OnApprovalAsync(DesktopApproval? approval) =>
        _dispatch(() =>
        {
            _approval = approval;
            OnPropertyChanged(nameof(HasApproval));
            OnPropertyChanged(nameof(ApprovalText));
            if (approval is not null) Status = "等待审批";
        });

    private void ApplyEvent(DesktopEvent update)
    {
        switch (update.Kind)
        {
            case "text" when !string.IsNullOrEmpty(update.Text) :
                EnsureStream(update.RunId).AppendText(update.Text);
                break;
            case "reasoning" when !string.IsNullOrEmpty(update.Text) :
                EnsureStream(update.RunId).AppendReasoning(update.Text);
                break;
            case "state" when update.RunId is { } runId :
                _activeRunId = runId;
                if (_submittedDraftRevision is { } submitted && _draftRevision == submitted) Draft = string.Empty;
                _submittedDraftRevision = null;
                if (!_runLabels.ContainsKey(runId) && _runtime?.Host.Session.ModelSelection is { } selection)
                {
                    _runLabels[runId] = SelectionLabel(selection);
                    NotifyModelSelection();
                }

                Status = update.Text ?? "运行中";
                break;
            case "state" : Status = update.Text ?? "运行中"; break;
            case "error" :
                _runError = update.Text ?? "运行失败";
                Notice    = _runError;
                break;
            case "workspace-repair" when update.WorkspaceRepair is { } repair : ApplyWorkspaceRepair(repair); break;
            case "history" when update.History is { } history : ApplyHistory(history); break;
        }
    }

    /// <summary>
    ///     <para>未分类的流式内容先放入过程组，正式历史到达后再决定正文归属。</para>
    ///     <para>Keeps unclassified streaming content in the process group until recorded history determines its placement.</para>
    /// </summary>
    private MessageItemViewModel EnsureStream(string? runId)
    {
        if (_stream is null)
        {
            var group = EnsureProcessGroup(runId ?? _activeRunId ?? "unidentified-run", -1);
            _stream = MessageItemViewModel.CreateStreaming();
            _stream.MarkInProcessGroup();
            group.Items.Add(_stream);
        }

        return _stream;
    }

    internal void ApplyHistory(SessionHistoryItem item)
    {
        if (!_historyIds.Add(item.Id)) return;
        if (item.Kind is SessionHistoryKind.User or SessionHistoryKind.Process or SessionHistoryKind.Final)
        {
            if (item.Kind is SessionHistoryKind.Process or SessionHistoryKind.Final) StopStream(true);
            var role = item.Kind == SessionHistoryKind.User ? MessageRole.User : MessageRole.Assistant;
            if (role == MessageRole.User && Title == "新对话" && !string.IsNullOrWhiteSpace(item.Text))
            {
                var firstLine = item.Text.Split('\n')[0];
                Title = firstLine[..Math.Min(firstLine.Length, 48)];
            }

            var message = new MessageItemViewModel(new ConversationMessage(item.Sequence, item.Id, role,
                                                                           item.Text ?? string.Empty,
                                                                           DateTimeOffset.Now,
                                                                           Reasoning : item.Reasoning));
            if (role == MessageRole.User)
            {
                Items.Add(message);
            }
            else
            {
                var group = EnsureProcessGroup(item.RunId ?? item.TurnId, item.Sequence);
                if (item.Kind == SessionHistoryKind.Process)
                {
                    message.MarkInProcessGroup();
                    group.Items.Add(message);
                }
                else
                {
                    if (message.GetProcessReasoningProjection() is { } reasoning) group.Items.Add(reasoning);
                    Items.Add(message);
                }
            }
        }
        else if (item.Kind == SessionHistoryKind.Tool)
        {
            var key       = $"{item.RunId}:{item.InvocationId ?? item.CallId}";
            var succeeded = item.Succeeded == true && item.State != "UnknownResult";
            var status = item.State switch
            {
                "UnknownResult" => ToolActivityStatus.Unknown,
                "KnownNotStarted" => item.Approval?.Contains("den", StringComparison.OrdinalIgnoreCase) == true
                    ? ToolActivityStatus.Denied
                    : ToolActivityStatus.NotStarted,
                _ => succeeded ? ToolActivityStatus.Succeeded : ToolActivityStatus.Failed
            };
            var activity = new ToolActivity(item.Sequence, key, item.ToolName ?? "tool", item.ArgumentsJson,
                                            status,
                                            item.Text ?? item.Stdout,
                                            item.NotStartedReason ??
                                            item.PreparationError ?? (succeeded ? null : item.State),
                                            DateTimeOffset.Now);
            if (_tools.TryGetValue(key, out var existing))
            {
                existing.Settle(activity);
            }
            else
            {
                var tool = new ToolActivityItemViewModel(activity);
                _tools.Add(key, tool);
                tool.MarkInProcessGroup();
                EnsureProcessGroup(item.RunId ?? item.TurnId, item.Sequence).Items.Add(tool);
            }
        }
    }

    private void StopStream(bool replace = false)
    {
        if (_stream is null) return;
        if (replace)
        {
            foreach (var group in _processGroups.Values)
                if (group.Items.Remove(_stream))
                    break;
        }
        else
        {
            _stream.MarkInterrupted();
        }

        _stream = null;
    }

    private ProcessGroupItemViewModel EnsureProcessGroup(string runId, long sequence)
    {
        if (_processGroups.TryGetValue(runId, out var existing)) return existing;
        var group = new ProcessGroupItemViewModel(sequence, runId, _runLabels.GetValueOrDefault(runId),
                                                  IsRunning && runId == _activeRunId);
        _processGroups.Add(runId, group);
        Items.Add(group);
        return group;
    }

    private void Respond(ApprovalAction action)
    {
        if (_approval is { } approval) _runtime?.Approvals.Respond(approval.Id, action);
    }

    private void UpdateNotices()
    {
        if (_runtime is null) return;
        var host = _runtime.Host;
        Notice = string.Join('\n', new[]
        {
            host.PersistenceWarning is { } warning ? $"本次未可靠保存：{warning}" : null,
            _runError,
            host.DisplayWarning, host.InspectionPendingNote,
            host.Session.Amendments.Any(item => item.CanonicalMessageIndex is null)
                ? "有已保存但未生效的补充；重启不会自动发送。"
                : null
        }.Where(value => !string.IsNullOrEmpty(value)));
        OnPropertyChanged(nameof(InspectionPending));
    }

    public void ShowError(Exception exception)
    {
        Notice = _factory.Redact(exception.Message);
        Status = "操作失败";
    }
}

/// <summary>
///     <para>用户配置中已声明的模型选项；不触发在线发现。</para>
///     <para>A model declared in user configuration; selecting it never triggers online discovery.</para>
/// </summary>
public sealed record ModelChoice(
    string                               ProfileName,
    string                               ModelId,
    string?                              DisplayName   = null,
    IReadOnlyList<ModelReasoningEffort>? Efforts       = null,
    ModelReasoningEffort?                DefaultEffort = null)
{
    public string Label => DisplayName ?? ModelId;
}
