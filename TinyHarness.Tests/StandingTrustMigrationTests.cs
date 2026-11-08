using System.Globalization;
using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Runtime;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         常设信任三态迁移（B4）：配置层检测/确认/漂移判定矩阵、普通保存不解禁、显式确认落盘形状
///         （RFC 3339 时间与范围修订哈希）、清除扩大设置的记录失效、新旧 permissions 形状往返兼容；
///         引擎侧三态门（联网与附加写根写）按生产同配对注入断言。全部走真实
///         <see cref="UserConfigStore" />/<see cref="PermissionEngine" />/<see cref="ResourceAccessPolicy" />，
///         不 mock 配置形状；夹具目录唯一并前置清理。
///     </para>
///     <para>
///         The standing-trust three-state migration (B4): the configuration-level detection/confirmation/
///         drift decision matrix, ordinary saves never unlocking the gate, the explicit confirmation record
///         shape on disk (RFC 3339 time plus the scope-revision hash), record ineffectiveness after clearing
///         the expansion settings, and round-trip compatibility across both permissions shapes; the engine's
///         three-state gates (networking and additional-write-root writes) assert with the production pairing.
///         Everything runs the real <see cref="UserConfigStore" />/<see cref="PermissionEngine" />/
///         <see cref="ResourceAccessPolicy" /> without mocking configuration shapes; fixture directories are
///         unique and cleaned up beforehand.
///     </para>
/// </summary>
public class StandingTrustMigrationTests
{
    // ---- 三态判定矩阵（真实 StandingTrustEvaluation） ----

    [Test]
    public void Evaluate_NoStandingExpansion_IsNone_RegardlessOfRecord()
    {
        var sandbox = new WindowsSandboxSettings { Enabled = true };
        var revision = StandingTrustEvaluation.ScopeRevision(sandbox);
        var record = new PermissionSettings
        {
            StandingTrustConfirmation = new StandingTrustConfirmation(DateTimeOffset.UtcNow, revision)
        };

        Assert.Multiple(() =>
        {
            // 沙箱未启用：整体 None，记录不产生效果。
            Assert.That(StandingTrustEvaluation.Evaluate(sandbox with { Enabled = false }, record),
                        Is.EqualTo(StandingTrustState.None));
            // 启用但只有 restricted 网络且无附加写根：无可扩大面，None。
            Assert.That(StandingTrustEvaluation.Evaluate(sandbox, record), Is.EqualTo(StandingTrustState.None));
            // 附加读根不触发门禁（广泛读基线下无扩大效果）。
            Assert.That(StandingTrustEvaluation.Evaluate(sandbox with
                                                         {
                                                             AdditionalReadRoots = [@"C:\trusted\read"]
                                                         },
                                                         record),
                        Is.EqualTo(StandingTrustState.None));
            // 无记录同样 None。
            Assert.That(StandingTrustEvaluation.Evaluate(sandbox, new PermissionSettings()),
                        Is.EqualTo(StandingTrustState.None));
        });
    }

    [Test]
    public void Evaluate_ExpansionWithoutMatchingRecord_IsUnconfirmed()
    {
        var networkTrust = new WindowsSandboxSettings
        {
            Enabled       = true,
            NetworkPolicy = SandboxNetworkPolicy.Enabled
        };
        var writeRoots = new WindowsSandboxSettings
        {
            Enabled              = true,
            AdditionalWriteRoots = [@"C:\trusted\write"]
        };
        var staleRecord = new PermissionSettings
        {
            StandingTrustConfirmation = new StandingTrustConfirmation(DateTimeOffset.UtcNow,
                                                                     new string('A', 64))
        };

        Assert.Multiple(() =>
        {
            // 常设网络信任命中且记录缺失 = Unconfirmed。
            Assert.That(StandingTrustEvaluation.Evaluate(networkTrust, new PermissionSettings()),
                        Is.EqualTo(StandingTrustState.Unconfirmed));
            // 附加写根非空且记录缺失 = Unconfirmed（网络 restricted 亦然）。
            Assert.That(StandingTrustEvaluation.Evaluate(writeRoots, new PermissionSettings()),
                        Is.EqualTo(StandingTrustState.Unconfirmed));
            // 记录存在但范围修订漂移（附加写根变化）= Unconfirmed。
            var drifted = writeRoots with { AdditionalWriteRoots = [@"C:\trusted\other"] };
            Assert.That(StandingTrustEvaluation.Evaluate(drifted, staleRecord),
                        Is.EqualTo(StandingTrustState.Unconfirmed));
        });
    }

    [Test]
    public void Evaluate_MatchingRecord_IsConfirmed_AndRevisionIsStableFingerprint()
    {
        var sandbox = new WindowsSandboxSettings
        {
            Enabled              = true,
            NetworkPolicy        = SandboxNetworkPolicy.Enabled,
            AdditionalReadRoots  = [@"C:\trusted\read", @"C:\trusted\read2"],
            AdditionalWriteRoots = [@"C:\trusted\write"]
        };
        var revision = StandingTrustEvaluation.ScopeRevision(sandbox);
        var record = new PermissionSettings
        {
            StandingTrustConfirmation = new StandingTrustConfirmation(DateTimeOffset.UtcNow, revision)
        };

        Assert.Multiple(() =>
        {
            Assert.That(StandingTrustEvaluation.Evaluate(sandbox, record),
                        Is.EqualTo(StandingTrustState.Confirmed));
            // 范围修订是 64 位大写 SHA-256 hex；内容不变则修订稳定，重排后归一亦不变。
            Assert.That(revision, Does.Match("^[0-9A-F]{64}$"));
            var reordered = sandbox with
            {
                AdditionalReadRoots = [@"C:\trusted\read2", @"C:\trusted\read"]
            };
            Assert.That(StandingTrustEvaluation.ScopeRevision(reordered), Is.EqualTo(revision));
            // 任一受指纹维度变化都会改变修订。
            Assert.That(StandingTrustEvaluation.ScopeRevision(sandbox with
                                                              {
                                                                  AdditionalWriteRoots = [@"C:\trusted\write2"]
                                                              }),
                        Is.Not.EqualTo(revision));
            Assert.That(StandingTrustEvaluation.ScopeRevision(sandbox with
                                                              {
                                                                  NetworkPolicy = SandboxNetworkPolicy.Restricted
                                                              }),
                        Is.Not.EqualTo(revision));
        });
    }

    // ---- 普通保存设置不解禁 ----

    [Test]
    public async Task OrdinarySettingsSave_DoesNotUnlockTheGate_OnlyExplicitRecordDoes()
    {
        using var fixture = ConfigFixture.Create();
        var sandbox = new WindowsSandboxSettings
        {
            Enabled       = true,
            NetworkPolicy = SandboxNetworkPolicy.Enabled
        };
        await UserConfigStore.SaveAsync(fixture.ConfigPath,
                                        new UserConfig
                                        {
                                            Settings = new UserConfigSettings { WindowsSandbox = sandbox }
                                        },
                                        CancellationToken.None);

        // 普通保存无关权限设置（不写确认记录）后仍 Unconfirmed：保存动作本身不解禁。
        var loaded = await UserConfigStore.LoadAsync(fixture.ConfigPath, CancellationToken.None);
        var saved = loaded with
        {
            Settings = (loaded.Settings ?? new UserConfigSettings()) with
            {
                Permissions = (loaded.Settings?.Permissions ?? new PermissionSettings()) with
                {
                    DefaultApprovalMode = SessionApprovalMode.AIAutoReview
                }
            }
        };
        await UserConfigStore.SaveAsync(fixture.ConfigPath, saved, CancellationToken.None);
        var reloaded = await UserConfigStore.LoadAsync(fixture.ConfigPath, CancellationToken.None);
        Assert.That(StandingTrustEvaluation.Evaluate(reloaded.Settings!.WindowsSandbox,
                                                     reloaded.Settings.Permissions),
                    Is.EqualTo(StandingTrustState.Unconfirmed),
                    "普通保存不得把未确认态变成任何免审批态。");

        // 对照：只有显式写入匹配范围的确认记录才解禁。
        var baselinePermissions = reloaded.Settings?.Permissions ?? new PermissionSettings();
        var confirmed = reloaded with
        {
            Settings = (reloaded.Settings ?? new UserConfigSettings()) with
            {
                Permissions = baselinePermissions with
                {
                    StandingTrustConfirmation = new StandingTrustConfirmation(DateTimeOffset.UtcNow,
                                                                             StandingTrustEvaluation
                                                                                .ScopeRevision(sandbox))
                }
            }
        };
        await UserConfigStore.SaveAsync(fixture.ConfigPath, confirmed, CancellationToken.None);
        var unlocked = await UserConfigStore.LoadAsync(fixture.ConfigPath, CancellationToken.None);
        Assert.That(StandingTrustEvaluation.Evaluate(unlocked.Settings!.WindowsSandbox,
                                                     unlocked.Settings.Permissions),
                    Is.EqualTo(StandingTrustState.Confirmed));
    }

    // ---- 显式确认落盘形状 ----

    [Test]
    public async Task ExplicitConfirmation_PersistsRfc3339TimeAndScopeRevisionHash()
    {
        using var fixture = ConfigFixture.Create();
        var sandbox = new WindowsSandboxSettings
        {
            Enabled              = true,
            AdditionalWriteRoots = [fixture.Root]
        };
        await UserConfigStore.SaveAsync(fixture.ConfigPath,
                                        new UserConfig
                                        {
                                            Settings = new UserConfigSettings { WindowsSandbox = sandbox }
                                        },
                                        CancellationToken.None);

        // 与 CLI chat / Desktop 确认入口同形：当前时间 + 确认时范围修订。
        var revision = StandingTrustEvaluation.ScopeRevision(sandbox);
        var confirmedAt = DateTimeOffset.Parse("2026-11-08T01:02:03.4567890+00:00",
                                               CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var loaded = await UserConfigStore.LoadAsync(fixture.ConfigPath, CancellationToken.None);
        var candidate = loaded with
        {
            Settings = (loaded.Settings ?? new UserConfigSettings()) with
            {
                Permissions = (loaded.Settings?.Permissions ?? new PermissionSettings()) with
                {
                    StandingTrustConfirmation = new StandingTrustConfirmation(confirmedAt, revision)
                }
            }
        };
        await UserConfigStore.SaveAsync(fixture.ConfigPath, candidate, CancellationToken.None);

        // 落盘形状：settings.permissions.standingTrust = { confirmedUtc(RFC 3339), scopeRevision(hex) }。
        var json = JsonNode.Parse(await File.ReadAllTextAsync(fixture.ConfigPath))!;
        var record = json["settings"]!["permissions"]!["standingTrust"]!.AsObject();
        Assert.Multiple(() =>
        {
            Assert.That(record["confirmedUtc"]!.GetValue<string>(),
                        Is.EqualTo("2026-11-08T01:02:03.4567890+00:00"),
                        "确认时间必须以 RFC 3339 往返格式落盘。");
            Assert.That(record["scopeRevision"]!.GetValue<string>(), Is.EqualTo(revision));
        });

        // 重新加载即回到 Confirmed。
        var reloaded = await UserConfigStore.LoadAsync(fixture.ConfigPath, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(reloaded.Settings!.Permissions!.StandingTrustConfirmation!.ConfirmedUtc,
                        Is.EqualTo(confirmedAt));
            Assert.That(StandingTrustEvaluation.Evaluate(reloaded.Settings.WindowsSandbox,
                                                         reloaded.Settings.Permissions),
                        Is.EqualTo(StandingTrustState.Confirmed));
        });
    }

    // ---- 清除扩大设置：记录失效 ----

    [Test]
    public async Task ClearedExpansionSettings_MakeTheRecordIneffective()
    {
        using var fixture = ConfigFixture.Create();
        var expanded = new WindowsSandboxSettings
        {
            Enabled       = true,
            NetworkPolicy = SandboxNetworkPolicy.Enabled,
            AdditionalWriteRoots = [fixture.Root]
        };
        var confirmed = new UserConfig
        {
            Settings = new UserConfigSettings
            {
                WindowsSandbox = expanded,
                Permissions = new PermissionSettings
                {
                    StandingTrustConfirmation = new StandingTrustConfirmation(DateTimeOffset.UtcNow,
                                                                             StandingTrustEvaluation
                                                                                .ScopeRevision(expanded))
                }
            }
        };
        await UserConfigStore.SaveAsync(fixture.ConfigPath, confirmed, CancellationToken.None);
        Assert.That(StandingTrustEvaluation.Evaluate(expanded,
                                                     confirmed.Settings!.Permissions),
                    Is.EqualTo(StandingTrustState.Confirmed));

        // 清除扩大设置（回 restricted/写根清空）后，即使记录仍在也回到 None；
        // 记录删除本身由撤销入口（Desktop 撤销/沙箱保存路径）完成，此处固化判定层的失效语义。
        var cleared = expanded with
        {
            NetworkPolicy        = SandboxNetworkPolicy.Restricted,
            AdditionalWriteRoots = []
        };
        Assert.That(StandingTrustEvaluation.Evaluate(cleared, confirmed.Settings.Permissions),
                    Is.EqualTo(StandingTrustState.None));
    }

    // ---- 双形状往返兼容 ----

    [Test]
    public async Task ConfigWithoutStandingTrustField_ParsesCleanly()
    {
        using var fixture = ConfigFixture.Create();
        await File.WriteAllTextAsync(fixture.ConfigPath,
                                     """{"settings":{"permissions":{"defaultApprovalMode":"HumanApproval"}}}""");

        var config = await UserConfigStore.LoadAsync(fixture.ConfigPath, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(config.Settings!.Permissions!.StandingTrustConfirmation, Is.Null);
            Assert.That(config.Settings.Permissions.DefaultApprovalMode,
                        Is.EqualTo(SessionApprovalMode.HumanApproval));
        });
    }

    [Test]
    public async Task StandingTrustRecord_RoundTrips_InBothPermissionShapes()
    {
        using var fixture = ConfigFixture.Create();
        var confirmedUtc = "2026-11-08T01:02:03.4567890+00:00";

        // 当前形状：defaultApprovalMode + standingTrust 记录。
        await File.WriteAllTextAsync(fixture.ConfigPath,
                                     new JsonObject
                                     {
                                         ["settings"] = new JsonObject
                                         {
                                             ["permissions"] = new JsonObject
                                             {
                                                 ["defaultApprovalMode"] = "AIAutoReview",
                                                 ["standingTrust"] = new JsonObject
                                                 {
                                                     ["confirmedUtc"] = confirmedUtc,
                                                     ["scopeRevision"] = "ABCD"
                                                 }
                                             }
                                         }
                                     }.ToJsonString());
        var current = await UserConfigStore.LoadAsync(fixture.ConfigPath, CancellationToken.None);
        Assert.That(current.Settings!.Permissions!.StandingTrustConfirmation!.ScopeRevision,
                    Is.EqualTo("ABCD"));

        // 保存再加载：当前形状下记录原样往返。
        await UserConfigStore.SaveAsync(fixture.ConfigPath, current, CancellationToken.None);
        var roundTripped = await UserConfigStore.LoadAsync(fixture.ConfigPath, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(roundTripped.Settings!.Permissions!.StandingTrustConfirmation!.ScopeRevision,
                        Is.EqualTo("ABCD"));
            Assert.That(roundTripped.Settings.Permissions.StandingTrustConfirmation!.ConfirmedUtc,
                        Is.EqualTo(current.Settings.Permissions.StandingTrustConfirmation!.ConfirmedUtc));
        });

        // 旧形状（迁移待确认）同样绑定确认记录：迁移确认不吞掉常设信任记录。
        await File.WriteAllTextAsync(fixture.ConfigPath,
                                     new JsonObject
                                     {
                                         ["settings"] = new JsonObject
                                         {
                                             ["permissions"] = new JsonObject
                                             {
                                                 ["defaultMode"] = "ReadOnly",
                                                 ["composerModes"] = new JsonArray("ReadOnly"),
                                                 ["standingTrust"] = new JsonObject
                                                 {
                                                     ["confirmedUtc"] = confirmedUtc,
                                                     ["scopeRevision"] = "ABCD"
                                                 }
                                             }
                                         }
                                     }.ToJsonString());
        var legacy = await UserConfigStore.LoadAsync(fixture.ConfigPath, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(legacy.Settings!.Permissions!.PendingMigration, Is.True);
            Assert.That(legacy.Settings.Permissions.StandingTrustConfirmation!.ScopeRevision,
                        Is.EqualTo("ABCD"));
        });

        // 旧形状保存保持旧字段与记录并存（原样保留语义）。
        await UserConfigStore.SaveAsync(fixture.ConfigPath, legacy, CancellationToken.None);
        var legacyJson = JsonNode.Parse(await File.ReadAllTextAsync(fixture.ConfigPath))!
                                 ["settings"]!["permissions"]!.AsObject();
        Assert.Multiple(() =>
        {
            Assert.That(legacyJson.ContainsKey("defaultMode"), Is.True);
            Assert.That(legacyJson["standingTrust"], Is.Not.Null);
        });
    }

    [TestCase("""{"settings":{"permissions":{"standingTrust":"yes"}}}""")]
    [TestCase("""{"settings":{"permissions":{"standingTrust":{}}}}""")]
    [TestCase("""{"settings":{"permissions":{"standingTrust":{"scopeRevision":"ABCD"}}}}""")]
    [TestCase("""{"settings":{"permissions":{"standingTrust":{"confirmedUtc":"not-a-timestamp","scopeRevision":"A"}}}}""")]
    [TestCase("""{"settings":{"permissions":{"standingTrust":{"confirmedUtc":"2026-11-08T01:02:03+00:00"}}}}""")]
    public async Task MalformedStandingTrustRecords_AreRejected(string json)
    {
        using var fixture = ConfigFixture.Create();
        await File.WriteAllTextAsync(fixture.ConfigPath, json);

        var error = await TestAssert.ThrowsAsync<InvalidDataException>(() =>
            UserConfigStore.LoadAsync(fixture.ConfigPath, CancellationToken.None));
        Assert.That(error!.Message, Does.Contain("standingTrust"));
    }

    // ---- 引擎三态门：联网 ----

    [Test]
    public void Engine_NetworkGate_FollowsTheThreeStates()
    {
        using var dir = new TestTempDir();
        var preparation = NetworkedPreparation(dir.Root);

        // 常设网络已启用（networkPolicy=enabled 的生产组合）时按三态判定。
        var none = CreateEngine(dir.Root, StandingTrustState.None, standingNetworkEnabled : true)
                  .Evaluate(preparation);
        var unconfirmed = CreateEngine(dir.Root, StandingTrustState.Unconfirmed, standingNetworkEnabled : true)
                  .Evaluate(preparation);
        var confirmed = CreateEngine(dir.Root, StandingTrustState.Confirmed, standingNetworkEnabled : true)
                  .Evaluate(preparation);

        // None 与 Unconfirmed 都逐次审批（Level2 + network-access 缺口）；只有 Unconfirmed 的文案
        // 注明"常设信任未确认"语义标记。
        Assert.Multiple(() =>
        {
            Assert.That(none.Decision, Is.EqualTo(PermissionDecision.Ask));
            Assert.That(none.Level, Is.EqualTo(PermissionLevel.Level2));
            Assert.That(none.MissingCapabilities, Does.Contain("network-access"));
            Assert.That(none.Reason, Does.Contain("network access"));
            Assert.That(none.Reason, Does.Not.Contain("unconfirmed"));

            Assert.That(unconfirmed.Decision, Is.EqualTo(PermissionDecision.Ask));
            Assert.That(unconfirmed.Level, Is.EqualTo(PermissionLevel.Level2));
            Assert.That(unconfirmed.Reason, Does.Contain("standing trust is unconfirmed"),
                        "未确认态的审批文案必须注明常设信任未确认。");

            Assert.That(confirmed.Decision, Is.EqualTo(PermissionDecision.Allow));
            Assert.That(confirmed.Level, Is.EqualTo(PermissionLevel.Level1));
        });
    }

    [Test]
    public void Engine_NetworkGate_ConfirmedWriteRoots_NeverExemptNetworkWhenStandingNetworkRestricted()
    {
        using var dir = new TestTempDir();
        var preparation = NetworkedPreparation(dir.Root);

        // 常设网络 restricted（组合实际消费 restricted）：即便写根把状态推到 Confirmed，
        // 网络申请仍逐次审批，文案如实说明 restricted，不复用"常设信任未确认"措辞。
        var confirmed = CreateEngine(dir.Root, StandingTrustState.Confirmed).Evaluate(preparation);
        var unconfirmed = CreateEngine(dir.Root, StandingTrustState.Unconfirmed).Evaluate(preparation);

        Assert.Multiple(() =>
        {
            Assert.That(confirmed.Decision, Is.EqualTo(PermissionDecision.Ask));
            Assert.That(confirmed.Level, Is.EqualTo(PermissionLevel.Level2));
            Assert.That(confirmed.MissingCapabilities, Does.Contain("network-access"));
            Assert.That(confirmed.Reason, Does.Contain("networkPolicy=restricted"));
            Assert.That(confirmed.Reason, Does.Not.Contain("unconfirmed"),
                        "常设网络未启用时不得复用未确认措辞误导审批者。");

            Assert.That(unconfirmed.Decision, Is.EqualTo(PermissionDecision.Ask));
            Assert.That(unconfirmed.Level, Is.EqualTo(PermissionLevel.Level2));
            Assert.That(unconfirmed.Reason, Does.Contain("networkPolicy=restricted"));
            Assert.That(unconfirmed.Reason, Does.Not.Contain("unconfirmed"));

            // 文件工具分支同样不复用未确认措辞。
            var fileToolEngine = CreateEngine(dir.Root, StandingTrustState.Confirmed);
            var fileInvocation = new ToolPreparation
            {
                ToolName              = "shell",
                CallId                = "call_1",
                Arguments             = new JsonObject { ["networkAccess"] = "enabled" },
                Capability            = "filesystem.read",
                Summary               = "networked read",
                RequiresNetworkAccess = true,
                TargetPaths           = [dir.Root]
            };
            var fileTool = fileToolEngine.Evaluate(fileInvocation);
            Assert.That(fileTool.Decision, Is.EqualTo(PermissionDecision.Ask));
            Assert.That(fileTool.Level, Is.EqualTo(PermissionLevel.Level2));
            Assert.That(fileTool.Reason, Does.Contain("networkPolicy=restricted"));
            Assert.That(fileTool.Reason, Does.Not.Contain("unconfirmed"));
        });
    }

    // ---- 引擎三态门：附加写根写 ----

    [Test]
    public void Engine_AdditionalWriteRootWrites_FollowTheThreeStates()
    {
        using var workspace = new TestTempDir();
        using var writeRoot = new TestTempDir();
        var target = Path.Combine(writeRoot.Root, "artifact.txt");
        var preparation = FileWritePreparation(target);

        // 生产组合点把同一状态同时注入策略与引擎；这里按同一配对断言。
        var unconfirmed = EvaluateWith(workspace, StandingTrustState.Unconfirmed, preparation);
        var none = EvaluateWith(workspace, StandingTrustState.None, preparation);
        var confirmed = EvaluateWith(workspace, StandingTrustState.Confirmed, preparation);

        Assert.Multiple(() =>
        {
            Assert.That(unconfirmed.Decision, Is.EqualTo(PermissionDecision.Ask));
            Assert.That(unconfirmed.Level, Is.EqualTo(PermissionLevel.Level2));
            Assert.That(unconfirmed.MissingCapabilities, Does.Contain("additional-path-write"));
            Assert.That(unconfirmed.Reason, Does.Contain("Standing trust is unconfirmed"));

            Assert.That(none.Decision, Is.EqualTo(PermissionDecision.Ask));
            Assert.That(none.Level, Is.EqualTo(PermissionLevel.Level2));
            Assert.That(none.MissingCapabilities, Does.Contain("additional-path-write"));

            Assert.That(confirmed.Decision, Is.EqualTo(PermissionDecision.Allow));
            Assert.That(confirmed.Level, Is.EqualTo(PermissionLevel.Level1));
        });
    }

    // ---- 组合矩阵：网络策略 × 确认状态 × 写根（真实求值链） ----

    [Test]
    public void Engine_NetworkGate_Matrix_PolicyConfirmationAndWriteRoots()
    {
        using var workspace = new TestTempDir();
        foreach (var policyKind in new[] { SandboxNetworkPolicy.Enabled, SandboxNetworkPolicy.Restricted })
        foreach (var writeRootsPresent in new[] { false, true })
        {
            var settings = new WindowsSandboxSettings
            {
                Enabled              = true,
                NetworkPolicy        = policyKind,
                AdditionalWriteRoots = writeRootsPresent ? [Path.Combine(workspace.Root, "standing")] : []
            };
            // 真实求值链：确认记录按当前设置计算范围修订后注入（None 用例不带记录，
            // Unconfirmed 用例带漂移修订），不得手工注入 Confirmed。
            var confirmation = new StandingTrustConfirmation(DateTimeOffset.UtcNow,
                                                             StandingTrustEvaluation.ScopeRevision(settings));
            var confirmedRecord    = new PermissionSettings { StandingTrustConfirmation = confirmation };
            var unconfirmedRecord  = new PermissionSettings
            {
                StandingTrustConfirmation = confirmation with { ScopeRevision = new string('B', 64) }
            };
            var states = new[]
            {
                (StandingTrustEvaluation.Evaluate(settings, new PermissionSettings()), new PermissionSettings()),
                (StandingTrustEvaluation.Evaluate(settings, unconfirmedRecord), unconfirmedRecord),
                (StandingTrustEvaluation.Evaluate(settings, confirmedRecord), confirmedRecord)
            };
            foreach (var (state, record) in states)
            {
                // 与生产组合同源：EffectiveSandboxSettings 决定组合实际消费的网络策略，
                // 引擎的常设网络开关与之一致。
                var effective = StandingTrustEvaluation.EffectiveSandboxSettings(settings, state);
                var standingNetworkEnabled = effective?.NetworkPolicy == SandboxNetworkPolicy.Enabled;
                var policy = new ResourceAccessPolicy(workspace.Root,
                                                      additionalWriteRoots : effective?.AdditionalWriteRoots,
                                                      standingTrust        : state);
                var engine = new PermissionEngine(workspace.Root,
                                                  standingTrust           : state,
                                                  standingNetworkEnabled  : standingNetworkEnabled,
                                                  resourcePolicy          : policy);
                var evaluation = engine.Evaluate(NetworkedPreparation(workspace.Root));

                // 期望：免审批当且仅当"组合实际消费 enabled + Confirmed"。Enabled+Unconfirmed 经
                // EffectiveSandboxSettings 回落 restricted，与 restricted 配置同样逐次审批（Level2 +
                // network-access 缺口），文案如实说明常设网络未启用，不复用未确认措辞。
                var exempt = standingNetworkEnabled && state == StandingTrustState.Confirmed;
                var expectedDecision = exempt ? PermissionDecision.Allow : PermissionDecision.Ask;
                var expectedLevel = exempt ? PermissionLevel.Level1 : PermissionLevel.Level2;
                Assert.Multiple(() =>
                {
                    Assert.That(evaluation.Decision, Is.EqualTo(expectedDecision),
                                $"policy={policyKind} writeRoots={writeRootsPresent} state={state}");
                    Assert.That(evaluation.Level, Is.EqualTo(expectedLevel),
                                $"policy={policyKind} writeRoots={writeRootsPresent} state={state}");
                    if (exempt) return;

                    Assert.That(evaluation.MissingCapabilities, Does.Contain("network-access"),
                                $"policy={policyKind} writeRoots={writeRootsPresent} state={state}");
                    Assert.That(evaluation.Reason, Does.Contain("networkPolicy=restricted"),
                                $"policy={policyKind} writeRoots={writeRootsPresent} state={state}");
                    Assert.That(evaluation.Reason, Does.Not.Contain("unconfirmed"),
                                $"policy={policyKind} writeRoots={writeRootsPresent} state={state}");
                });
            }
        }
    }

    // ---- 夹具与工具 ----

    private static PermissionEngine CreateEngine(string workspaceRoot, StandingTrustState state,
                                                 bool standingNetworkEnabled = false) =>
        new(workspaceRoot, standingTrust : state, standingNetworkEnabled : standingNetworkEnabled);

    private static PermissionEvaluation EvaluateWith(TestTempDir workspace, StandingTrustState state,
                                                    ToolPreparation preparation)
    {
        var policy = new ResourceAccessPolicy(workspace.Root,
                                              additionalWriteRoots : [Path.GetDirectoryName(preparation.TargetPaths[0])!],
                                              standingTrust        : state);
        return new PermissionEngine(workspace.Root, standingTrust : state, resourcePolicy : policy)
            .Evaluate(preparation);
    }

    private static ToolPreparation NetworkedPreparation(string root) => new()
    {
        ToolName              = "shell",
        CallId                = "call_1",
        Arguments             = new JsonObject { ["networkAccess"] = "enabled" },
        Capability            = "process.execute",
        Summary               = "networked command",
        RequiresNetworkAccess = true,
        TargetPaths           = [root]
    };

    private static ToolPreparation FileWritePreparation(string target) => new()
    {
        ToolName    = "apply_patch",
        CallId      = "call_1",
        Arguments   = new JsonObject { ["patch"] = "placeholder" },
        Capability  = "filesystem.write",
        Summary     = "write artifact",
        TargetPaths = [target]
    };

    /// <summary>
    ///     <para>唯一临时目录中的用户配置夹具；目录即配置目录，文件前置清理由 Dispose 负责。</para>
    ///     <para>
    ///         A user-config fixture in a unique temp directory; the directory is the config directory and
    ///         Dispose performs the beforehand cleanup contract.
    ///     </para>
    /// </summary>
    private sealed record ConfigFixture(string Root, string ConfigPath) : IDisposable
    {
        public static ConfigFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"tinyharness-standing-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            return new ConfigFixture(root, System.IO.Path.Combine(root, UserConfigStore.FileName));
        }

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
