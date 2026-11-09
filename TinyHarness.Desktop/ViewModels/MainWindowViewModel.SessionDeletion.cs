using System.Diagnostics;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private int              _sessionDeletionDialogVersion;
    private string?          _sessionDeletionErrorText;
    private bool             _sessionDeletionOpen;
    private bool             _sessionDeletionRunning;
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
    public string SessionDeletionPrompt => "此操作会永久删除会话记录，且无法恢复。";
    public string? SessionDeletionErrorText
    {
        get => _sessionDeletionErrorText;
        private set
        {
            if (!SetProperty(ref _sessionDeletionErrorText, value)) return;
            OnPropertyChanged(nameof(HasSessionDeletionError));
        }
    }

    public bool HasSessionDeletionError => SessionDeletionErrorText is not null;
    public bool CanConfirmSessionDeletion => !IsSessionDeletionRunning && _sessionDeletionTarget is not null;

    /// <summary>
    ///     <para>只为当前归档视图中的正式归档会话打开永久删除确认。</para>
    ///     <para>Opens permanent-deletion confirmation only for an established archived session in the archive view.</para>
    /// </summary>
    private void OpenSessionDeletion(SessionViewModel? session)
    {
        if (!IsArchiveView || session is not { IsArchived: true, CanPin: true } ||
            !Sessions.Contains(session) || IsSessionDeletionRunning || !CanUseArchiveState(session.StoreDirectory!))
            return;

        _sessionDeletionTarget = session;
        _sessionDeletionDialogVersion++;
        SessionDeletionErrorText = null;
        IsSessionDeletionOpen = true;
        NotifySessionDeletionProperties();
    }

    /// <summary>
    ///     <para>释放本地宿主后持有会话 writer 锁删除日志，再清理归档、置顶、索引与旧侧栏投影。</para>
    ///     <para>Deletes the log under its writer lock after releasing the local host, then clears archive, pin, index, and legacy sidebar projections.</para>
    /// </summary>
    private async Task ConfirmSessionDeletionAsync()
    {
        if (!CanConfirmSessionDeletion || _sessionDeletionTarget is not { } session ||
            session.Id is not { } sessionId || session.StoreDirectory is not { } storeDirectory)
            return;

        if (!session.TryBeginDeleteMutation())
        {
            SessionDeletionErrorText = "会话正在打开、运行或切换；请等待操作完成后重试。";
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

            await session.EnsureHistoryLoadedAsync();
            await session.ReleaseRuntimeForDeletionAsync();
            if (DeleteSessionLogOverride is { } deleteSessionLog)
                await deleteSessionLog(protectedStoreDirectory, workspaceRoot, sessionId, _lifetime.Token);
            else
                await SessionStoreDeletion.DeleteAsync(protectedStoreDirectory, workspaceRoot, sessionId,
                                                       _lifetime.Token);

            var cleanupIssues = new List<string>();
            await RemoveDeletedSessionProjectionAsync(protectedStoreDirectory, sessionId, cleanupIssues);
            Sessions.Remove(session);
            RebuildProjects();
            if (ReferenceEquals(SelectedSession, session))
                SelectedSession = ConversationDraft();

            if (cleanupIssues.Count > 0)
                Notice = $"会话记录已删除；部分侧栏投影清理失败：{string.Join("；", cleanupIssues)}";
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

    private async Task RemoveDeletedSessionProjectionAsync(
        string storeDirectory, string sessionId, ICollection<string> cleanupIssues)
    {
        await _pinStateMutationGate.WaitAsync(CancellationToken.None);
        try
        {
            try
            {
                var archives = await SessionStoreArchiveStore.SetArchivedAsync(
                    storeDirectory, sessionId, false, CancellationToken.None);
                SetStoreArchiveState(storeDirectory, archives);
            }
            catch (Exception exception)
            {
                cleanupIssues.Add("归档状态");
                Trace.TraceError("Session archive cleanup for '{0}' failed: {1}", storeDirectory, exception.Message);
            }

            try
            {
                var pins = await SessionStorePinStore.SetSessionPinnedAsync(
                    storeDirectory, sessionId, false, CancellationToken.None);
                SetStorePinState(storeDirectory, pins);
            }
            catch (Exception exception)
            {
                cleanupIssues.Add("置顶状态");
                Trace.TraceError("Session pin cleanup for '{0}' failed: {1}", storeDirectory, exception.Message);
            }
        }
        finally
        {
            _pinnedSessionIdsByStore.Clear();
            _archivedSessionIdsByStore.Clear();
            _pinStateMutationGate.Release();
        }

        try
        {
            var index = new SessionIndexStore(storeDirectory);
            var availability = await index.GetAvailabilityAsync(CancellationToken.None);
            if (availability.Status == SessionIndexAvailabilityStatus.Available)
                await index.RemoveAsync(sessionId, CancellationToken.None);
        }
        catch (Exception exception) when (exception is SessionIndexException or IOException or
                                           UnauthorizedAccessException)
        {
            cleanupIssues.Add("索引");
            Trace.TraceError("Session index cleanup for '{0}' failed: {1}", storeDirectory, exception.Message);
        }

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
        }
        catch (Exception exception)
        {
            cleanupIssues.Add("旧侧栏顺序");
            Trace.TraceError("Legacy sidebar cleanup for '{0}' failed: {1}", storeDirectory, exception.Message);
        }
    }

    private void CancelSessionDeletion()
    {
        if (IsSessionDeletionRunning) return;
        CloseSessionDeletion();
    }

    private void CloseSessionDeletion()
    {
        IsSessionDeletionOpen = false;
        _sessionDeletionTarget = null;
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
