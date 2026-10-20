using System.ComponentModel;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Cli.Services;

/// <summary>
///     <para>协调宿主目录确认、权限检查及执行栈激活；不调用模型或自动 provisioning。</para>
///     <para>
///         Coordinates host directory consent, permission checks and execution activation without model calls or
///         automatic provisioning.
///     </para>
/// </summary>
internal sealed class WorkspaceAccessCoordinator
{
    private static readonly ProcessExecutionPolicy ReadOnlyPolicy = new()
    {
        Backend        = ProcessExecutionBackendKind.Host,
        DisplayName    = "workspace read-only (process execution disabled)",
        PolicyIdentity = "workspace-read-only;process-disabled"
    };

    private readonly Func<Task<(ShellTool Tool, ProcessExecutionPolicy Policy)>> _activate;

    private readonly ShellTool           _disabledShell;
    private readonly IWorkspaceAclGrant  _grant;
    private readonly TextReader          _input;
    private readonly bool                _interactive;
    private readonly TextWriter          _output;
    private readonly PermissionEngine    _permissions;
    private readonly bool                _requiresAcl;
    private readonly WorkspaceTrustStore _store;
    private          bool                _grantUncertain;

    /// <summary>
    ///     <para>组合宿主输入和可替换授权器；构造时不读取信任文件或启动沙箱。</para>
    ///     <para>
    ///         Combines host input and a replaceable grant provider without reading trust or starting a sandbox during
    ///         construction.
    ///     </para>
    /// </summary>
    public WorkspaceAccessCoordinator(WorkspaceAccessState state, WorkspaceTrustStore store,
                                      PermissionEngine permissions, TextReader input, TextWriter output,
                                      bool interactive,
                                      bool requiresAcl, IWorkspaceAclGrant grant,
                                      Func<Task<(ShellTool Tool, ProcessExecutionPolicy Policy)>> activate)
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
    public ShellTool              Shell  { get; private set; }
    public ProcessExecutionPolicy Policy { get; private set; } = ReadOnlyPolicy;

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

            await
                _output.WriteLineAsync("Unknown workspace: read-only. Open an interactive chat and use /trust to authorize it.");
            return;
        }

        await ApplyAsync(trusted.Value, false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     <para>在空闲输入边界处理 /trust，不发送给模型，不重放旧工具。</para>
    ///     <para>Handles /trust at an idle input boundary without sending it to a model or replaying tools.</para>
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
            await
                _output.WriteLineAsync("Directory authorization may still be running. Access remains read-only; inspect and restart before retrying.");
            return;
        }

        State.Identity.EnsureUnchanged();
        await _output.WriteLineAsync($"Workspace: {TerminalDisplay.Format(State.Identity.Path)}");
        await _output.WriteLineAsync("Trust this directory? Tools still require their normal approvals.");
        await _output.WriteAsync("[t] trust and allow changes, [r] read-only (default), [q] exit: ");
        await _output.FlushAsync(cancellationToken);
        var choice = (await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim().ToLowerInvariant();
        if (choice is null or "q")
            throw new OperationCanceledException("Workspace consent was cancelled.", cancellationToken);
        if (choice is not ("t" or "r" or ""))
        {
            await _output.WriteLineAsync("Unrecognized choice; access was not changed.");
            return;
        }

        await ApplyAsync(choice == "t", true, cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyAsync(bool trusted, bool persist, CancellationToken cancellationToken)
    {
        State.SetReadOnly(true);
        Shell  = _disabledShell;
        Policy = ReadOnlyPolicy;
        _permissions.ClearTemporaryAllows();
        if (persist) await _store.SaveAsync(State.Identity, trusted, cancellationToken).ConfigureAwait(false);
        if (!trusted)
        {
            await _output.WriteLineAsync("Workspace access: read-only; patch and shell are disabled.");
            return;
        }

        try
        {
            State.Identity.EnsureUnchanged();
            if (_requiresAcl && !await _grant.CanChangePermissionsAsync(State.Identity, cancellationToken)
                                             .ConfigureAwait(false))
            {
                await
                    _output.WriteLineAsync($"Sandbox ACL preparation requires READ_CONTROL | WRITE_DAC and consistent child inheritance for {TerminalDisplay.Format(State.Identity.Path)}.");
                await
                    _output.WriteLineAsync("Authorization adds directory-only WRITE_DAC for your original account, merges this workspace's sandbox write ACEs, and propagates inherited permissions to existing unprotected children. Owner, explicit child rules and metadata deny rules are preserved. No setup process, provisioning or project command.");
                if (!_interactive)
                {
                    await
                        _output
                           .WriteLineAsync("Directory authorization needs interactive consent; remaining read-only.");
                    return;
                }

                await _output.WriteAsync("Request Windows UAC for this directory? [y/N]: ");
                await _output.FlushAsync(cancellationToken);
                if (!string.Equals((await _input.ReadLineAsync(cancellationToken))?.Trim(), "y",
                                   StringComparison.OrdinalIgnoreCase))
                {
                    await _output.WriteLineAsync("Directory authorization declined; remaining read-only.");
                    return;
                }

                var outcome = await _grant.GrantAsync(State.Identity, cancellationToken).ConfigureAwait(false);
                if (outcome != WorkspaceGrantOutcome.Completed)
                {
                    _grantUncertain = outcome == WorkspaceGrantOutcome.DidNotFinish;
                    await
                        _output
                           .WriteLineAsync($"Directory authorization: {outcome}; remaining read-only. No command was started.");
                    return;
                }

                if (!await _grant.CanChangePermissionsAsync(State.Identity, cancellationToken).ConfigureAwait(false))
                    throw new
                        InvalidOperationException("Directory authorization completed but workspace ACL preparation is still incomplete.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var execution = await _activate().ConfigureAwait(false);
            State.Identity.EnsureUnchanged();
            Shell  = execution.Tool;
            Policy = execution.Policy;
            State.SetReadOnly(false);
            await
                _output.WriteLineAsync($"Workspace access: trusted; {Policy.DisplayName}. Normal tool approvals remain required.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                      or Win32Exception)
        {
            await
                _output.WriteLineAsync($"Workspace activation failed; remaining read-only: {TerminalDisplay.Format(ex.Message)}");
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
