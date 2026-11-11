using System.Diagnostics;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private int               _sessionDeletionDialogVersion;
    private string?           _sessionDeletionErrorText;
    private bool              _sessionDeletionOpen;
    private bool              _sessionDeletionRunning;
    private SessionViewModel? _sessionDeletionTarget;

    internal Func<string, string, string, CancellationToken, Task>? DeleteSessionLogOverride { get; set; }

    public RelayCommand<SessionViewModel> OpenSessionDeletionCommand    { get; }
    public RelayCommand                   ConfirmSessionDeletionCommand { get; }
    public RelayCommand                   CancelSessionDeletionCommand  { get; }

    public bool IsSessionDeletionOpen
    {
        get => _sessionDeletionOpen;
        private set => SetProperty(ref _sessionDeletionOpen, value);
    }

    public bool IsSessionDeletionRunning
    {
        get => _sessionDeletionRunning;
        private set
        {
            if (!SetProperty(ref _sessionDeletionRunning, value)) return;
            OnPropertyChanged(nameof(CanConfirmSessionDeletion));
        }
    }

    public string SessionDeletionTargetName => _sessionDeletionTarget?.Title ?? string.Empty;
    public string SessionDeletionPrompt     => "此操作会永久删除会话记录，且无法恢复。";

    public string? SessionDeletionErrorText
    {
        get => _sessionDeletionErrorText;
        private set
        {
            if (!SetProperty(ref _sessionDeletionErrorText, value)) return;
            OnPropertyChanged(nameof(HasSessionDeletionError));
        }
    }

    public bool HasSessionDeletionError   => SessionDeletionErrorText is not null;
    public bool CanConfirmSessionDeletion => !IsSessionDeletionRunning && _sessionDeletionTarget is not null;

    /// <summary>
    ///     <para>只允许删除当前归档视图中的正式归档会话；按通用设置显示确认或直接删除。</para>
    ///     <para>Deletes only established archived sessions in the archive view, with confirmation when enabled.</para>
    /// </summary>
    private void OpenSessionDeletion(SessionViewModel? session)
    {
        if (!IsArchiveView              || session is not { IsArchived: true, CanPin: true } ||
            !Sessions.Contains(session) || IsSessionDeletionRunning || !CanUseArchiveState(session.StoreDirectory!))
            return;

        _sessionDeletionTarget = session;
        _sessionDeletionDialogVersion++;
        SessionDeletionErrorText = null;
        IsSessionDeletionOpen    = Settings.General.ShowArchivedSessionDeletionPrompt;
        NotifySessionDeletionProperties();
        if (!IsSessionDeletionOpen) _ = ConfirmSessionDeletionAsync();
    }

    /// <summary>
    ///     <para>释放本地宿主后持有会话 writer 锁删除日志，再清理归档、置顶、索引与旧侧栏投影。</para>
    ///     <para>
    ///         Deletes the log under its writer lock after releasing the local host, then clears archive, pin, index, and
    ///         legacy sidebar projections.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         删除不预读历史：日志内容严格性属于恢复路径，损坏日志也可被用户明确地永久删除。
    ///     </para>
    ///     <para>
    ///         Deletion never preloads history: journal strictness belongs to restore paths, and a damaged
    ///         journal can still be permanently deleted by explicit user intent.
    ///     </para>
    /// </remarks>
    private async Task ConfirmSessionDeletionAsync()
    {
        if (!CanConfirmSessionDeletion      || _sessionDeletionTarget is not { } session ||
            session.Id is not { } sessionId || session.StoreDirectory is not { } storeDirectory)
            return;

        if (!session.TryBeginDeleteMutation())
        {
            const string message = "会话正在打开、运行或切换；请等待操作完成后重试。";
            if (IsSessionDeletionOpen) SessionDeletionErrorText = message;
            else Notice                                         = message;
            return;
        }

        var dialogVersion = _sessionDeletionDialogVersion;
        var workspaceRoot = session.WorkspaceRoot;
        IsSessionDeletionRunning = true;
        SessionDeletionErrorText = null;
        var refreshAcquired = false;
        try
        {
            await _refreshGate.WaitAsync(_lifetime.Token);
            refreshAcquired = true;
            var protectedStoreDirectory = await ResolveProtectedDirectoryAsync(workspaceRoot, _lifetime.Token);
            if (!SameSidebarStore(protectedStoreDirectory, storeDirectory))
                throw new SessionStoreException(
                                                "The configured session-store directory changed before session deletion; reload history and retry.");

            // 历史加载只服务 UI 投影：删除、资源释放与并发保护都不依赖它，选中与续聊路径会按需加载。
            await session.ReleaseRuntimeForDeletionAsync();
            IReadOnlyList<SessionStoreCleanupIssue> coreCleanupIssues;
            if (DeleteSessionLogOverride is { } deleteSessionLog)
            {
                await deleteSessionLog(protectedStoreDirectory, workspaceRoot, sessionId, _lifetime.Token);
                coreCleanupIssues =
                    await _storeOperations.CleanupDeletedSessionProjectionsAsync(protectedStoreDirectory, sessionId);
            }
            else
            {
                coreCleanupIssues = await _storeOperations.DeleteSessionPermanentlyAsync(protectedStoreDirectory,
                         workspaceRoot, sessionId,
                         _lifetime.Token);
            }

            // 旧侧栏偏好清理留在桌面资产内；失败并入清理提示，顺序保持在 Core 投影之后。
            var cleanupIssues = new List<SessionStoreCleanupIssue>(coreCleanupIssues);
            var legacyIssue   = await RemoveDeletedSessionLegacyPreferencesAsync(protectedStoreDirectory, sessionId);
            if (legacyIssue is not null) cleanupIssues.Add(legacyIssue);
            Sessions.Remove(session);
            RebuildProjects();
            if (ReferenceEquals(SelectedSession, session))
                SelectedSession = ConversationDraft();

            if (cleanupIssues.Count > 0)
                Notice = $"会话记录已删除；部分侧栏投影清理失败：{DescribeSessionCleanupIssues(cleanupIssues)}";
            if (_sessionDeletionDialogVersion == dialogVersion) CloseSessionDeletion();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // 窗口关闭时结束尚未取得日志删除锁的操作。
        }
        catch (Exception exception)
        {
            if (_sessionDeletionDialogVersion == dialogVersion && IsSessionDeletionOpen)
                SessionDeletionErrorText = _factory.Redact(exception.Message);
            else
                Notice = _factory.Redact(exception.Message);
        }
        finally
        {
            if (refreshAcquired) _refreshGate.Release();
            session.EndDeleteMutation();
            IsSessionDeletionRunning = false;
            NotifySessionDeletionProperties();
        }
    }

    /// <summary>
    ///     <para>把 Core 清理区域映射为界面提示标签，保持既有提示顺序与文案。</para>
    ///     <para>Maps Core cleanup areas to notice labels, preserving the established order and wording.</para>
    /// </summary>
    private static string DescribeSessionCleanupIssues(IReadOnlyList<SessionStoreCleanupIssue> issues) =>
        string.Join("；", issues.Select(issue => issue.Area switch
        {
            SessionStoreCleanupArea.ArchiveState => "归档状态",
            SessionStoreCleanupArea.PinState     => "置顶状态",
            SessionStoreCleanupArea.SessionIndex => "索引",
            _                                    => "旧侧栏顺序"
        }));

    /// <summary>
    ///     <para>清理桌面侧栏偏好中已删除会话的旧顺序与旧置顶引用；失败返回清理问题而不抛出。</para>
    ///     <para>
    ///         Clears the deleted session's legacy order and pin references from desktop preferences; a failure
    ///         returns a cleanup issue instead of throwing.
    ///     </para>
    /// </summary>
    private async Task<SessionStoreCleanupIssue?> RemoveDeletedSessionLegacyPreferencesAsync(
        string storeDirectory, string sessionId)
    {
        try
        {
            await EnsureSidebarPreferencesLoadedAsync(CancellationToken.None);
            if (!_sidebarPreferencesSaveAllowed)
                throw new InvalidOperationException("The sidebar preferences are unavailable.");

            UpdateStoreSidebarPreferences(storeDirectory, preferences => preferences with
            {
                ManualSessionOrder = preferences.ManualSessionOrder
                                                .Where(id => id != sessionId).ToArray(),
                LegacyPinnedSessionIds = preferences.LegacyPinnedSessionIds
                                                    .Where(id => id != sessionId).ToArray()
            });
            QueueSidebarPreferencesSave(true);
            await FlushSidebarPreferencesAsync();
            return null;
        }
        catch (Exception exception)
        {
            Trace.TraceError("Legacy sidebar cleanup for '{0}' failed: {1}", storeDirectory, exception.Message);
            return new SessionStoreCleanupIssue(SessionStoreCleanupArea.LegacySidebarOrder, exception.Message);
        }
    }

    private void CancelSessionDeletion()
    {
        if (IsSessionDeletionRunning) return;
        CloseSessionDeletion();
    }

    private void CloseSessionDeletion()
    {
        IsSessionDeletionOpen    = false;
        _sessionDeletionTarget   = null;
        SessionDeletionErrorText = null;
        _sessionDeletionDialogVersion++;
        NotifySessionDeletionProperties();
    }

    private void NotifySessionDeletionProperties()
    {
        OnPropertyChanged(nameof(SessionDeletionTargetName));
        OnPropertyChanged(nameof(SessionDeletionPrompt));
        OnPropertyChanged(nameof(CanConfirmSessionDeletion));
        OnPropertyChanged(nameof(HasSessionDeletionError));
    }
}
