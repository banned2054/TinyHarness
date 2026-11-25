using System.ComponentModel;
using System.Text.Json;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Cli.Services;

/// <summary>
///     <para>协调宿主目录确认、按需 ACL 准备及执行栈激活；不调用模型或自动 provisioning。</para>
///     <para>
///         Coordinates host directory consent, on-demand ACL preparation and execution activation without model calls or
///         automatic provisioning.
///     </para>
/// </summary>
internal sealed class WorkspaceAccessCoordinator : IDisposable
{
    private static readonly ProcessExecutionPolicy ReadOnlyPolicy = new()
    {
        Backend        = ProcessExecutionBackendKind.Host,
        DisplayName    = "workspace read-only (process execution disabled)",
        PolicyIdentity = "workspace-read-only;process-disabled"
    };

    private readonly Func<GrantRegistry, Task<(ITool Tool, ProcessExecutionPolicy Policy)>> _activate;

    private readonly Lock                        _cacheLock = new();
    private readonly ShellTool                   _disabledShell;
    private readonly IWorkspaceAclGrant          _grant;
    private readonly TextReader                  _input;
    private readonly bool                        _interactive;
    private readonly TextWriter                  _output;
    private readonly SemaphoreSlim               _prepareLock = new(1, 1);
    private readonly bool                        _requiresAcl;
    private readonly WorkspaceTrustStore         _store;
    private          volatile bool               _grantUncertain;
    private          PendingAclRepair?           _pendingAclRepair;
    private          PermissionEngine            _permissions;
    private          WorkspaceDirectoryIdentity? _preparedIdentity;
    private          string?                     _preparedPolicyIdentity;

    /// <summary>
    ///     <para>组合宿主输入和可替换授权器；构造时不读取信任文件或启动沙箱。</para>
    ///     <para>
    ///         Combines host input and a replaceable grant provider without reading trust or starting a sandbox during
    ///         construction.
    ///     </para>
    /// </summary>
    public WorkspaceAccessCoordinator(WorkspaceAccessState state,       WorkspaceTrustStore store,
                                      PermissionEngine     permissions, TextReader          input, TextWriter output,
                                      bool                 interactive,
                                      bool                 requiresAcl, IWorkspaceAclGrant grant,
                                      Func<GrantRegistry, Task<(ITool Tool, ProcessExecutionPolicy Policy)>>
                                          activate)
    {
        State        = state;
        _store       = store;
        _permissions = permissions;
        _input       = input;
        _output      = output;
        _interactive = interactive;
        _requiresAcl = requiresAcl;
        _grant       = grant;
        _activate    = activate;
        _disabledShell = new ShellTool(new Workspace(state.Identity.Path), processBackend : new DisabledBackend(),
                                       executionPolicy : Policy);
        Shell = _disabledShell;
    }

    public WorkspaceAccessState   State  { get; }
    public ITool                  Shell  { get; private set; }
    public ProcessExecutionPolicy Policy { get; private set; } = ReadOnlyPolicy;

    /// <summary>
    ///     <para>释放用于串行化工作区 ACL 准备的信号量。</para>
    ///     <para>Releases the semaphore that serializes workspace ACL preparation.</para>
    /// </summary>
    public void Dispose() => _prepareLock.Dispose();

    /// <summary>
    ///     <para>包装沙箱进程后端，使工作区可写执行在启动命令前按需核验并准备目录 ACL。</para>
    ///     <para>
    ///         Wraps a sandbox process backend so workspace-write executions verify and prepare directory ACLs on demand
    ///         before launching commands.
    ///     </para>
    /// </summary>
    public IProcessExecutionBackend WrapSandboxBackend(IProcessExecutionBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        return _requiresAcl ? new WorkspaceAclPreparingBackend(backend, this) : backend;
    }

    /// <summary>
    ///     <para>在启动可写沙箱命令前按需核验并准备当前工作区 ACL；全程不触发 UAC。</para>
    ///     <para>
    ///         Verifies and prepares current workspace ACLs on demand before launching a writable sandbox command without
    ///         triggering UAC.
    ///     </para>
    /// </summary>
    public async Task EnsureWorkspaceWriteAclReadyAsync(
        string effectivePolicyIdentity, CancellationToken cancellationToken)
    {
        if (!_requiresAcl) return;

        await _prepareLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_grantUncertain)
            {
                ClearPreparedAclState();
                throw new InvalidOperationException(
                    $"Directory authorization for '{TerminalDisplay.Format(State.Identity.Path)}' may still be running. " +
                    "Inspect permissions and restart before retrying. No command was started.");
            }

            var stage = "identity-validation";
            try
            {
                State.Identity.EnsureUnchanged();
                if (IsPreparedFor(State.Identity, effectivePolicyIdentity)) return;

                stage = "readiness-check";
                if (await _grant.IsReadyAsync(State.Identity, cancellationToken).ConfigureAwait(false))
                {
                    State.Identity.EnsureUnchanged();
                    MarkPrepared(State.Identity, effectivePolicyIdentity);
                    return;
                }

                stage = "scope-validation";
                _grant.ValidatePreparationScope(State.Identity);

                stage = "normal-privilege-preparation";
                await _grant.PrepareWithoutElevationAsync(State.Identity, cancellationToken).ConfigureAwait(false);

                stage = "readiness-verification";
                State.Identity.EnsureUnchanged();
                if (!await _grant.IsReadyAsync(State.Identity, cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException(
                        "Workspace ACL preparation completed without establishing sandbox readiness.");

                MarkPrepared(State.Identity, effectivePolicyIdentity);
            }
            catch (OperationCanceledException)
            {
                ClearPreparedAclState();
                throw;
            }
            catch (Exception ex) when (IsAclPreparationFailure(ex))
            {
                ClearPreparedAclState();
                RecordPendingRepair(State.Identity.Path, stage, ex.Message);
                throw new InvalidOperationException(
                    BuildPreparationFailureMessage(State.Identity.Path, stage, ex.Message), ex);
            }
        }
        finally
        {
            _prepareLock.Release();
        }
    }

    /// <summary>
    ///     <para>在单会话 CLI 边界切换当前权限引擎；激活 Shell 与该引擎使用同一 GrantRegistry。</para>
    ///     <para>Binds the active session's permission engine; the activated Shell shares its GrantRegistry.</para>
    /// </summary>
    public async Task BindSessionAsync(PermissionEngine permissions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        cancellationToken.ThrowIfCancellationRequested();
        ClearPreparedAclState();
        if (State.IsReadOnly)
        {
            _permissions.RevokeGrants();
            _permissions = permissions;
            Shell        = _disabledShell;
            Policy       = ReadOnlyPolicy;
            return;
        }

        // 候选激活失败或取消时保留旧宿主的引擎、Shell 与授权配对。
        State.Identity.EnsureUnchanged();
        var execution = await _activate(permissions.Grants).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        State.Identity.EnsureUnchanged();
        _permissions.RevokeGrants();
        _permissions = permissions;
        Shell        = execution.Tool;
        Policy       = execution.Policy;
        State.SetReadOnly(false);
    }

    /// <summary>
    ///     <para>读取已有决定，首次交互询问；非交互未知目录只读且不保存默认决定。</para>
    ///     <para>
    ///         Loads consent or prompts on first interactive use; unknown noninteractive workspaces remain read-only without
    ///         saving a default.
    ///     </para>
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var trusted = await _store.ReadAsync(State.Identity, cancellationToken).ConfigureAwait(false);
        if (trusted is null)
        {
            if (_interactive)
            {
                await ChangeAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            // 未信任工作区的入口提示走 stderr：run 保持可用（模型与只读文件工具），进程执行
            // 因沙箱未组合被引擎 fail-closed 拒绝，不回退宿主。
            await Console.Error.WriteLineAsync(
                                               "Unknown workspace: read-only. Open an interactive chat and use /trust to authorize it.");
            return;
        }

        await ApplyAsync(trusted.Value, false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>在空闲输入边界处理 /trust，不发送给模型，不重放旧工具；已可信且启用沙箱时提供显式 ACL 修复。</para>
    ///     <para>
    ///         Handles /trust at an idle input boundary without sending it to a model or replaying tools; offers explicit ACL
    ///         repair when trusted with the sandbox enabled.
    ///     </para>
    /// </summary>
    public async Task ChangeAsync(CancellationToken cancellationToken)
    {
        if (!_interactive)
        {
            await _output.WriteLineAsync("/trust requires interactive input; access was not changed.");
            return;
        }

        if (_grantUncertain)
        {
            await _output.WriteLineAsync(
                "Directory authorization may still be running; result is unknown. Inspect permissions and restart before retrying.");
            return;
        }

        State.Identity.EnsureUnchanged();
        var canRepairAcl = _requiresAcl && !State.IsReadOnly;
        if (canRepairAcl && ReadPendingRepair() is { } pending)
        {
            await _output.WriteLineAsync(
                $"Pending workspace ACL issue for '{TerminalDisplay.Format(pending.Directory)}' " +
                $"(stage '{pending.Stage}'): {TerminalDisplay.Format(pending.Reason)}");
        }

        await _output.WriteLineAsync($"Workspace: {TerminalDisplay.Format(State.Identity.Path)}");
        await _output.WriteLineAsync("Trust this directory? Tools still require their normal approvals.");
        var prompt = canRepairAcl
            ? "[a] repair workspace ACL, [t] trust and allow changes, [r] read-only (default), [q] exit: "
            : "[t] trust and allow changes, [r] read-only (default), [q] exit: ";
        await _output.WriteAsync(prompt);
        await _output.FlushAsync(cancellationToken);
        var choice = (await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim().ToLowerInvariant();
        if (choice is null or "q")
            throw new OperationCanceledException("Workspace consent was cancelled.", cancellationToken);
        if (canRepairAcl && choice == "a")
        {
            await RepairWorkspaceAclAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (choice is not ("t" or "r" or ""))
        {
            await _output.WriteLineAsync("Unrecognized choice; access was not changed.");
            return;
        }

        await ApplyAsync(choice == "t", true, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyAsync(bool trusted, bool persist, CancellationToken cancellationToken)
    {
        ClearPreparedAclState(clearPendingRepair : true);
        State.SetReadOnly(true);
        Shell  = _disabledShell;
        Policy = ReadOnlyPolicy;
        _permissions.RevokeGrants();
        if (persist) await _store.SaveAsync(State.Identity, trusted, cancellationToken).ConfigureAwait(false);
        if (!trusted)
        {
            await _output.WriteLineAsync("Workspace access: read-only; patch and shell are disabled.");
            return;
        }

        try
        {
            State.Identity.EnsureUnchanged();
            cancellationToken.ThrowIfCancellationRequested();
            var execution = await _activate(_permissions.Grants).ConfigureAwait(false);
            State.Identity.EnsureUnchanged();
            Shell  = execution.Tool;
            Policy = execution.Policy;
            State.SetReadOnly(false);
            await
                _output.WriteLineAsync($"Workspace access: trusted; {Policy.DisplayName}. Normal tool approvals remain required.");
        }
        // JsonException（读 cap_sid 遇损坏表）与既有四类同转为激活失败语义，保持只读不逃逸。
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                      or Win32Exception or JsonException or InvalidDataException)
        {
            await
                _output.WriteLineAsync($"Workspace activation failed; remaining read-only: {TerminalDisplay.Format(ex.Message)}");
        }
    }

    private async Task RepairWorkspaceAclAsync(CancellationToken cancellationToken)
    {
        var acquired     = false;
        var grantStarted = false;
        try
        {
            await _prepareLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;

            State.Identity.EnsureUnchanged();
            _grant.ValidatePreparationScope(State.Identity);

            if (await _grant.IsReadyAsync(State.Identity, cancellationToken).ConfigureAwait(false))
            {
                State.Identity.EnsureUnchanged();
                MarkPrepared(State.Identity, Policy.PolicyIdentity);
                await _output.WriteLineAsync(
                    $"Workspace ACLs are already ready for '{TerminalDisplay.Format(State.Identity.Path)}'. No command was replayed; please retry your operation.");
                return;
            }

            if (await _grant.CanChangePermissionsAsync(State.Identity, cancellationToken).ConfigureAwait(false))
            {
                await _grant.PrepareWithoutElevationAsync(State.Identity, cancellationToken).ConfigureAwait(false);
                State.Identity.EnsureUnchanged();
                if (await _grant.IsReadyAsync(State.Identity, cancellationToken).ConfigureAwait(false))
                {
                    State.Identity.EnsureUnchanged();
                    MarkPrepared(State.Identity, Policy.PolicyIdentity);
                    await _output.WriteLineAsync(
                        $"Workspace ACL repair completed without elevation and was verified for '{TerminalDisplay.Format(State.Identity.Path)}'. No command was replayed; please retry your operation.");
                    return;
                }

                ClearPreparedAclState();
                await _output.WriteLineAsync(
                    $"Normal-privilege workspace ACL repair completed, but readiness verification did not pass for '{TerminalDisplay.Format(State.Identity.Path)}'. No command was started or replayed.");
                return;
            }

            await _output.WriteLineAsync(
                $"Sandbox ACL preparation requires READ_CONTROL | WRITE_DAC and consistent child inheritance for {TerminalDisplay.Format(State.Identity.Path)}.");
            await _output.WriteLineAsync(
                "Authorization adds directory-only WRITE_DAC for your original account, merges this workspace's sandbox write ACEs, and propagates inherited permissions to existing unprotected children. Owner, explicit child rules and metadata deny rules are preserved. No setup process, provisioning or project command.");
            await _output.WriteAsync("Request Windows UAC for this directory? [y/N]: ");
            await _output.FlushAsync(cancellationToken);
            if (!string.Equals((await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim(), "y",
                               StringComparison.OrdinalIgnoreCase))
            {
                await _output.WriteLineAsync(
                    "Directory authorization declined; workspace trust was unchanged and no command was started or replayed.");
                return;
            }

            grantStarted = true;
            var outcome = await _grant.GrantAsync(State.Identity, cancellationToken).ConfigureAwait(false);
            grantStarted = false;

            switch (outcome)
            {
                case WorkspaceGrantOutcome.Completed:
                    State.Identity.EnsureUnchanged();
                    if (await _grant.IsReadyAsync(State.Identity, cancellationToken).ConfigureAwait(false))
                    {
                        State.Identity.EnsureUnchanged();
                        MarkPrepared(State.Identity, Policy.PolicyIdentity);
                        await _output.WriteLineAsync(
                            $"Directory authorization completed and workspace ACLs were verified for '{TerminalDisplay.Format(State.Identity.Path)}'. No command was replayed; please retry your operation.");
                    }
                    else
                    {
                        ClearPreparedAclState();
                        await _output.WriteLineAsync(
                            $"Directory authorization completed, but workspace ACL verification is still incomplete for '{TerminalDisplay.Format(State.Identity.Path)}'. No command was started or replayed.");
                    }

                    break;
                case WorkspaceGrantOutcome.Declined:
                    ClearPreparedAclState();
                    await _output.WriteLineAsync(
                        "Directory authorization: Declined; no command was started or replayed. You may run /trust again later.");
                    break;
                case WorkspaceGrantOutcome.Failed:
                    ClearPreparedAclState();
                    await _output.WriteLineAsync(
                        "Directory authorization: Failed; no command was started or replayed. You may run /trust again later.");
                    break;
                case WorkspaceGrantOutcome.DidNotFinish:
                    _grantUncertain = true;
                    ClearPreparedAclState();
                    await _output.WriteLineAsync(
                        "Directory authorization: DidNotFinish. Authorization result is unknown; inspect permissions and restart before retrying.");
                    break;
            }
        }
        catch (WorkspaceGrantFailureException ex)
        {
            ClearPreparedAclState();
            await _output.WriteLineAsync(
                $"Directory authorization failed for '{TerminalDisplay.Format(State.Identity.Path)}': {TerminalDisplay.Format(ex.Message)} No command was started or replayed.");
        }
        catch (OperationCanceledException) when (grantStarted)
        {
            _grantUncertain = true;
            ClearPreparedAclState();
            await _output.WriteLineAsync(
                $"Directory authorization for '{TerminalDisplay.Format(State.Identity.Path)}' was interrupted; result is unknown. Inspect permissions and restart before retrying.");
        }
        catch (Exception ex) when (grantStarted && ex is not WorkspaceGrantFailureException)
        {
            _grantUncertain = true;
            ClearPreparedAclState();
            await _output.WriteLineAsync(
                $"Directory authorization for '{TerminalDisplay.Format(State.Identity.Path)}' ended unexpectedly ({TerminalDisplay.Format(ex.Message)}); result is unknown. Inspect permissions and restart before retrying.");
        }
        catch (OperationCanceledException)
        {
            await _output.WriteLineAsync(
                "Workspace ACL repair was cancelled; access was unchanged and no command was started or replayed.");
        }
        catch (Exception ex) when (IsAclPreparationFailure(ex))
        {
            ClearPreparedAclState();
            await _output.WriteLineAsync(
                $"Workspace ACL repair failed for '{TerminalDisplay.Format(State.Identity.Path)}': {TerminalDisplay.Format(ex.Message)} No command was started or replayed.");
        }
        finally
        {
            if (acquired) _prepareLock.Release();
        }
    }

    private string BuildPreparationFailureMessage(string directory, string stage, string reason)
    {
        var formattedDir    = TerminalDisplay.Format(directory);
        var formattedReason = TerminalDisplay.Format(reason);
        var followUp = _interactive
            ? "Run /trust at the idle prompt to explicitly repair workspace ACLs, then retry your request."
            : "Non-interactive runs never request Windows UAC; open an interactive chat and run /trust to repair workspace ACLs before retrying.";
        return $"Workspace ACL preparation failed for '{formattedDir}' at stage '{stage}': {formattedReason} " +
               $"No command was started. {followUp}";
    }

    private static bool IsAclPreparationFailure(Exception ex) =>
        ex is UnauthorizedAccessException or IOException or InvalidOperationException
            or Win32Exception or JsonException or WorkspaceAclPreparationException or InvalidDataException;

    private bool IsPreparedFor(WorkspaceDirectoryIdentity identity, string effectivePolicyIdentity)
    {
        lock (_cacheLock)
        {
            return _preparedIdentity == identity &&
                   string.Equals(_preparedPolicyIdentity, effectivePolicyIdentity, StringComparison.Ordinal);
        }
    }

    private void MarkPrepared(WorkspaceDirectoryIdentity identity, string effectivePolicyIdentity)
    {
        lock (_cacheLock)
        {
            _preparedIdentity       = identity;
            _preparedPolicyIdentity = effectivePolicyIdentity;
            _pendingAclRepair       = null;
        }
    }

    private void ClearPreparedAclState(bool clearPendingRepair = false)
    {
        lock (_cacheLock)
        {
            _preparedIdentity       = null;
            _preparedPolicyIdentity = null;
            if (clearPendingRepair) _pendingAclRepair = null;
        }
    }

    private void RecordPendingRepair(string directory, string stage, string reason)
    {
        lock (_cacheLock)
        {
            _pendingAclRepair = new PendingAclRepair(directory, stage, reason);
        }
    }

    private PendingAclRepair? ReadPendingRepair()
    {
        lock (_cacheLock)
        {
            return _pendingAclRepair;
        }
    }

    private void RecordPostRefreshAclFailure(WorkspaceAclPreparationException acl)
    {
        var directory = string.IsNullOrWhiteSpace(acl.Problem.WriteRoot) ? State.Identity.Path : acl.Problem.WriteRoot;
        lock (_cacheLock)
        {
            _preparedIdentity       = null;
            _preparedPolicyIdentity = null;
            _pendingAclRepair       = new PendingAclRepair(directory, "post-refresh-validation", acl.Message);
        }
    }

    private static string ComputeEffectivePolicyIdentity(
        string basePolicyIdentity, PreparedProcessExecution execution)
    {
        var identity = execution.GrantScope is { IsEmpty: false } scope
            ? WindowsSandboxComposer.AppendGrantSection(basePolicyIdentity, scope)
            : basePolicyIdentity;
        return execution.NetworkAccessEnabled
            ? identity + "\nnetworkAccess=enabled"
            : identity;
    }

    private sealed record PendingAclRepair(string Directory, string Stage, string Reason);

    private sealed class WorkspaceAclPreparingBackend(
        IProcessExecutionBackend   inner,
        WorkspaceAccessCoordinator coordinator) : IProcessExecutionBackend
    {
        public async Task<ProcessExecutionResult> ExecuteAsync(PreparedProcessExecution execution,
                                                               IProcessOutputCapture    stdoutCapture,
                                                               IProcessOutputCapture    stderrCapture,
                                                               CancellationToken        cancellationToken)
        {
            var effectivePolicyIdentity =
                ComputeEffectivePolicyIdentity(coordinator.Policy.PolicyIdentity, execution);
            await coordinator.EnsureWorkspaceWriteAclReadyAsync(effectivePolicyIdentity, cancellationToken)
                             .ConfigureAwait(false);
            try
            {
                return await inner.ExecuteAsync(execution, stdoutCapture, stderrCapture, cancellationToken)
                                  .ConfigureAwait(false);
            }
            catch (Exception ex) when (FindAclPreparationException(ex) is { } acl)
            {
                // 刷新后复检发现 ACL 不一致时清除准备缓存并记录待修复项，供后续 /trust 修复。
                coordinator.RecordPostRefreshAclFailure(acl);
                throw;
            }
        }

        private static WorkspaceAclPreparationException? FindAclPreparationException(Exception exception)
        {
            for (var current = exception; current is not null; current = current.InnerException)
                if (current is WorkspaceAclPreparationException acl)
                    return acl;
            return null;
        }
    }

    private sealed class DisabledBackend : IProcessExecutionBackend
    {
        public Task<ProcessExecutionResult> ExecuteAsync(PreparedProcessExecution execution,
                                                         IProcessOutputCapture    stdoutCapture,
                                                         IProcessOutputCapture    stderrCapture,
                                                         CancellationToken        cancellationToken) =>
            throw new InvalidOperationException("Process execution is disabled for a read-only workspace.");
    }
}
