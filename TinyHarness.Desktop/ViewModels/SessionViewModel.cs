using Avalonia.Threading;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using System.Collections.ObjectModel;
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
    private readonly DesktopSessionManager _manager;
    private readonly DesktopSessionFactory                         _factory;
    private readonly Func<Action, ValueTask> _dispatch;
    private readonly HashSet<string>                               _historyIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolActivityItemViewModel> _tools     = new(StringComparer.Ordinal);
    private DesktopSession?                              _runtime;
    private string _draft = string.Empty;
    private string _status = "就绪";
    private string _notice = string.Empty;
    private string _workspaceRoot;
    private bool _running;
    private bool _opening;
    private bool _allowChanges;
    private bool _showReasoning;
    private DesktopApproval? _approval;
    private SessionHistoryCursor? _historyCursor;
    private bool _historyLoaded;
    private MessageItemViewModel? _stream;
    private long _draftRevision;
    private string _title;
    private ModelChoice? _model;
    private Task? _historyLoad;
    private Task? _completion;
    private bool _changing;
    private ModelReasoningEffort? _effort;
    private SessionModelSelection? _savedSelection;
    private SessionModelIdentity? _historicalIdentity;
    private readonly Dictionary<string, string> _runLabels = new(StringComparer.Ordinal);
    private CancellationTokenSource? _openingCancellation;
    private long? _submittedDraftRevision;

    public SessionViewModel(DesktopSessionManager manager, DesktopSessionFactory factory, string workspaceRoot,
                             string? id = null, string? storeDirectory = null, string? title = null,
                             Func<Action, ValueTask>? dispatch = null)
    {
        _manager           = manager;
        _factory           = factory;
        _dispatch          = dispatch ?? (async action => await Dispatcher.UIThread.InvokeAsync(action));
        _workspaceRoot     = workspaceRoot;
        Id                 = id;
        StoreDirectory     = storeDirectory;
        _title             = title ?? "新对话";
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
    }

    public string?                                         Id { get; private set; }
    public string?                                         StoreDirectory { get; private set; }
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
            if (SetProperty(ref _workspaceRoot, value)) OnPropertyChanged(nameof(WorkspaceLabel));
        }
    }

    public bool AllowChanges
    {
        get => _allowChanges;
        set
        {
            if (_allowChanges == value) return;
            if (!CanConfigure) throw new InvalidOperationException("当前会话的权限设置已经生效。");
            if (SetProperty(ref _allowChanges, value)) OnPropertyChanged(nameof(PermissionPickerLabel));
        }
    }

    public bool ShowReasoning
    {
        get => _showReasoning;
        set
        {
            if (_showReasoning == value) return;
            if (!CanConfigure) throw new InvalidOperationException("当前会话的思考展示设置已经生效。");
            SetProperty(ref _showReasoning, value);
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
            if (SetProperty(ref _running, value)) OnPropertyChanged(nameof(CanChangeModel));
        }
    }

    public bool HasMoreHistory    => _historyCursor is not null;
    public bool InspectionPending => _runtime?.Host.InspectionPending == true;
    public bool HasApproval       => _approval is not null;

    public string ApprovalText => _approval is { } approval
        ? $"{approval.ToolName}\n{approval.Summary}\n{approval.Arguments}\n{string.Join('\n', approval.TargetPaths)}\n{approval.ExecutionPolicy}"
        : string.Empty;

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
                _savedSelection     = stored.ModelSelection;
                _historicalIdentity = stored.Model;
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
        if (page.Warning is { } warning) Notice = warning;
        OnPropertyChanged(nameof(HasMoreHistory));
    }

    public Task EnsureHistoryLoadedAsync() => _historyLoad ??= LoadHistoryAsync();

    /// <summary>
    ///     <para>捕获本页草稿和版本后发送；运行中输入走 Core 补充入口，完成只消费原始草稿。</para>
    ///     <para>Captures this page's draft and revision before sending; active input uses Core amendments and consumes only that draft.</para>
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
            _completion             = CompleteRunAsync(_runtime!.SubmitAsync(text));
        }
        catch
        {
            IsRunning = false;
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
            Status = run.Result?.Status.ToString() ?? "运行结束";
            StopStream();
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
            IsRunning = false;
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
                EnsureStream().AppendText(update.Text);
                break;
            case "reasoning" when !string.IsNullOrEmpty(update.Text) :
                EnsureStream().AppendReasoning(update.Text);
                break;
            case "state" when update.RunId is { } runId :
                if (_submittedDraftRevision is { } submitted && _draftRevision == submitted) Draft = string.Empty;
                _submittedDraftRevision = null;
                if (!_runLabels.ContainsKey(runId) && _runtime?.Host.Session.ModelSelection is { } selection)
                {
                    _runLabels[runId] = SelectionLabel(selection);
                    NotifyModelSelection();
                }

                Status = update.Text ?? "运行中";
                break;
            case "state" :                                      Status = update.Text ?? "运行中"; break;
            case "error" :                                      Notice = update.Text ?? "运行失败"; break;
            case "history" when update.History is { } history : ApplyHistory(history); break;
        }
    }

    /// <summary>取当前流式气泡；正文与思考增量到达时惰性创建，条目落位或收束后不再复活。</summary>
    private MessageItemViewModel EnsureStream()
    {
        if (_stream is null)
        {
            _stream = MessageItemViewModel.CreateStreaming();
            Items.Add(_stream);
        }

        return _stream;
    }

    private void ApplyHistory(SessionHistoryItem item)
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

            Items.Add(new MessageItemViewModel(new ConversationMessage(item.Sequence, item.Id, role,
                                                                       item.Text ?? string.Empty, DateTimeOffset.Now,
                                                                       Reasoning : _showReasoning
                                                                           ? item.Reasoning
                                                                           : null,
                                                                       ModelLabel : item.RunId is { } runId
                                                                           ? _runLabels.GetValueOrDefault(runId)
                                                                           : null)));
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
            if (_tools.TryGetValue(key, out var existing)) existing.Settle(activity);
            else {
                var tool = new ToolActivityItemViewModel(activity);
                _tools.Add(key, tool);
                Items.Add(tool);
            }
        }
    }

    private void StopStream(bool replace = false)
    {
        if (_stream is null) return;
        if (replace) Items.Remove(_stream);
        else _stream.MarkInterrupted();
        _stream = null;
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
    public string Label => $"{ProfileName} / {DisplayName ?? ModelId}";
}
