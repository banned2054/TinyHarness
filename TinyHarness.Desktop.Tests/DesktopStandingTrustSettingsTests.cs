using NUnit.Framework;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Tests;

/// <summary>
///     <para>
///         权限设置页常设授权区块（B4）：待确认态展示并显式确认落盘（范围修订哈希）、撤销=网络回
///         restricted 并清空附加写根且删除确认记录（附加读根保留）、普通保存原样保留记录不解禁；
///         沙箱设置页清除扩大设置时记录随保存删除并回到 None。
///     </para>
///     <para>
///         The permission-settings standing-authorization block (B4): pending display with explicit
///         confirmation persistence (scope-revision hash), revoke = network back to restricted, additional
///         write roots cleared, and the confirmation record deleted (additional read roots kept); ordinary
///         saves keep the record and never unlock the gate; clearing the expansion settings in the sandbox
///         page deletes the record on save and returns to None.
///     </para>
/// </summary>
[TestFixture]
public sealed class DesktopStandingTrustSettingsTests
{
    [Test]
    public async Task ConfirmStandingTrust_PersistsTheRecordAndActivatesTheBlock()
    {
        using var fixture = await ConfigFixture.CreateAsync();
        var editor = new PermissionSettingsViewModel(fixture.Config);
        await editor.LoadAsync();

        // 待确认区块可见：Pending、非 Active，说明文本含写根与"待确认"语义标记。
        Assert.Multiple(() =>
        {
            Assert.That(editor.StandingTrustVisible, Is.True);
            Assert.That(editor.StandingTrustPending, Is.True);
            Assert.That(editor.StandingTrustActive, Is.False);
            Assert.That(editor.StandingTrustSummary, Does.Contain(fixture.WriteRoot));
            Assert.That(editor.StandingTrustSummary, Does.Contain("待确认"));
        });

        await editor.ConfirmStandingTrustAsync();

        var reloaded = await UserConfigStore.LoadAsync(fixture.Config, default);
        var record = reloaded.Settings!.Permissions!.StandingTrustConfirmation;
        Assert.Multiple(() =>
        {
            Assert.That(record, Is.Not.Null);
            Assert.That(record!.ScopeRevision,
                        Is.EqualTo(StandingTrustEvaluation.ScopeRevision(reloaded.Settings.WindowsSandbox!)),
                        "确认记录必须携带确认时的范围修订。");
            // 区块进入已确认态。
            Assert.That(editor.StandingTrustActive, Is.True);
            Assert.That(editor.StandingTrustPending, Is.False);
            Assert.That(editor.StandingTrustSummary, Does.Contain("已确认"));
        });
    }

    [Test]
    public async Task RevokeStandingTrust_ClearsSettingsDeletesRecord_KeepsReadRoots()
    {
        using var fixture = await ConfigFixture.CreateAsync();
        var editor = new PermissionSettingsViewModel(fixture.Config);
        await editor.LoadAsync();
        await editor.ConfirmStandingTrustAsync();

        await editor.RevokeStandingTrustAsync();

        var reloaded = await UserConfigStore.LoadAsync(fixture.Config, default);
        var sandbox = reloaded.Settings!.WindowsSandbox!;
        Assert.Multiple(() =>
        {
            Assert.That(sandbox.NetworkPolicy, Is.EqualTo(SandboxNetworkPolicy.Restricted));
            Assert.That(sandbox.AdditionalWriteRoots, Is.Empty);
            Assert.That(sandbox.AdditionalReadRoots, Is.EqualTo(new[] { fixture.ReadRoot }),
                        "撤销只清除扩大面，附加读根保持原值。");
            Assert.That(reloaded.Settings.Permissions!.StandingTrustConfirmation, Is.Null);
            // 区块回到隐藏（None）。
            Assert.That(editor.StandingTrustVisible, Is.False);
            Assert.That(editor.StandingState, Is.EqualTo(StandingTrustState.None));
        });
    }

    [Test]
    public async Task OrdinarySave_PreservesTheConfirmationRecord_NeverUnlocksOrRevokes()
    {
        using var fixture = await ConfigFixture.CreateAsync();
        var editor = new PermissionSettingsViewModel(fixture.Config);
        await editor.LoadAsync();
        await editor.ConfirmStandingTrustAsync();
        var confirmedRecord =
            (await UserConfigStore.LoadAsync(fixture.Config, default)).Settings!.Permissions!
               .StandingTrustConfirmation;

        // 普通保存（改默认审批模式）不得解禁也不得撤销：记录原样保留。
        editor.DefaultMode = editor.Modes.Single(row => row.Mode == SessionApprovalMode.AIAutoReview);
        await editor.SaveAsync();

        var reloaded = await UserConfigStore.LoadAsync(fixture.Config, default);
        Assert.Multiple(() =>
        {
            Assert.That(reloaded.Settings!.Permissions!.StandingTrustConfirmation, Is.EqualTo(confirmedRecord));
            Assert.That(StandingTrustEvaluation.Evaluate(reloaded.Settings.WindowsSandbox,
                                                         reloaded.Settings.Permissions),
                        Is.EqualTo(StandingTrustState.Confirmed));
            Assert.That(reloaded.Settings.Permissions.DefaultApprovalMode,
                        Is.EqualTo(SessionApprovalMode.AIAutoReview));
            Assert.That(editor.StandingTrustActive, Is.True);
        });
    }

    [Test]
    public async Task SandboxSettingsSave_ClearingExpansionSettings_DeletesTheRecord()
    {
        using var fixture = await ConfigFixture.CreateAsync();
        var editor = new PermissionSettingsViewModel(fixture.Config);
        await editor.LoadAsync();
        await editor.ConfirmStandingTrustAsync();

        // 沙箱设置页：网络回 restricted 且写根清空保存，扩大设置清除即解除门禁（记录随保存删除）。
        var sandbox = new WindowsSandboxSettingsViewModel(fixture.Config);
        await sandbox.LoadAsync();
        sandbox.SelectedNetworkPolicy =
            sandbox.NetworkPolicyOptions.Single(option => option.Policy == SandboxNetworkPolicy.Restricted);
        sandbox.WriteRootsText = string.Empty;
        await sandbox.SaveAsync();

        var reloaded = await UserConfigStore.LoadAsync(fixture.Config, default);
        Assert.Multiple(() =>
        {
            Assert.That(reloaded.Settings!.WindowsSandbox!.NetworkPolicy,
                        Is.EqualTo(SandboxNetworkPolicy.Restricted));
            Assert.That(reloaded.Settings.WindowsSandbox.AdditionalWriteRoots, Is.Empty);
            Assert.That(reloaded.Settings.Permissions!.StandingTrustConfirmation, Is.Null);
            Assert.That(StandingTrustEvaluation.Evaluate(reloaded.Settings.WindowsSandbox,
                                                         reloaded.Settings.Permissions),
                        Is.EqualTo(StandingTrustState.None));
        });
    }

    // ---- M2 确认展示：常设授权摘要含附加读根列表与读根注入/撤销语义 ----

    [Test]
    public async Task StandingTrustSummary_ListsReadRoots_AndRevocationSemantics()
    {
        using var fixture = await ConfigFixture.CreateAsync();
        var editor = new PermissionSettingsViewModel(fixture.Config);
        await editor.LoadAsync();

        // 确认前摘要即列出附加读根行（结构化语义标记：注入 OS 读面 + profile 外可读）。
        var pendingSummary = editor.StandingTrustSummary;
        Assert.Multiple(() =>
        {
            Assert.That(pendingSummary, Does.Contain("附加读根："));
            Assert.That(pendingSummary, Does.Contain(fixture.ReadRoot));
            Assert.That(pendingSummary, Does.Contain("注入沙箱 OS 读面"));
            Assert.That(pendingSummary, Does.Contain("profile 外的根真实可读"));
            Assert.That(pendingSummary, Does.Contain("读根不再注入"),
                        "有效期行必须如实说明撤销后读根不再注入。");
        });

        // BuildStandingTrustSummary 为确认前基线核对单一来源：直接断言同一文本。
        var loaded = await UserConfigStore.LoadAsync(fixture.Config, default);
        Assert.That(PermissionSettingsViewModel.BuildStandingTrustSummary(loaded),
                    Is.EqualTo(pendingSummary));
    }

    /// <summary>
    ///     <para>唯一临时目录中的配置夹具：启用沙箱 + 常设在线 + 附加读写根（全部位于夹具目录内）。</para>
    ///     <para>
    ///         A config fixture in a unique temp directory: the sandbox enabled with standing online access
    ///         and additional read/write roots (all inside the fixture directory).
    ///     </para>
    /// </summary>
    private sealed class ConfigFixture(string root, string config, string readRoot, string writeRoot)
        : IDisposable
    {
        public static async Task<ConfigFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "tinyharness-desk-standing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var readRoot  = Path.Combine(root, "read-root");
            var writeRoot = Path.Combine(root, "write-root");
            Directory.CreateDirectory(readRoot);
            Directory.CreateDirectory(writeRoot);
            var fixture = new ConfigFixture(root, Path.Combine(root, "user-config.json"), readRoot, writeRoot);
            var sandbox = new WindowsSandboxSettings
            {
                Enabled              = true,
                NetworkPolicy        = SandboxNetworkPolicy.Enabled,
                AdditionalReadRoots  = [readRoot],
                AdditionalWriteRoots = [writeRoot]
            };
            await UserConfigStore.SaveAsync(fixture.Config,
                                            new UserConfig
                                            {
                                                Settings = new UserConfigSettings { WindowsSandbox = sandbox }
                                            },
                                            default);
            return fixture;
        }

        public string Root { get; } = root;

        public string Config { get; } = config;

        public string ReadRoot { get; } = readRoot;

        public string WriteRoot { get; } = writeRoot;

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 尽力清理临时目录。
            }
        }
    }
}
