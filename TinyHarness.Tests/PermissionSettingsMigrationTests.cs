using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         settings.permissions 新旧形状迁移测试（D9）：旧配置检测即置迁移待确认态且不拒载其余配置、
///         旧模式到审批模式的映射、迁移确认后改写为当前形状、混合形状与未知取值拒绝。
///     </para>
///     <para>
///         Migration tests for settings.permissions (D9): legacy configs load as pending-migration without
///         rejecting other fields, the legacy-mode to approval-mode mapping, the post-confirmation rewrite to
///         the current shape, and rejection of mixed shapes and unknown values.
///     </para>
/// </summary>
public class PermissionSettingsMigrationTests
{
    private static async Task WriteRawAsync(string path, string json)
    {
        await File.WriteAllTextAsync(path, json);
    }

    private static string NewConfigDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tinyharness-perm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<string> ReadConfigAsync(string path) => await File.ReadAllTextAsync(path);

    [Test]
    public async Task LegacyConfig_LoadsAsPendingMigrationWithoutRejectingOtherFields()
    {
        var dir  = NewConfigDir();
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            await WriteRawAsync(path,
                                """{"settings":{"sessionDirectory":"logs","permissions":{"defaultMode":"ProjectWrite","composerModes":["ReadOnly","ProjectWrite"]}}}""");

            var config = await UserConfigStore.LoadAsync(path, CancellationToken.None);

            var permissions = config.Settings!.Permissions!;
            Assert.That(permissions.PendingMigration, Is.True);
            Assert.That(permissions.LegacyDefaultMode, Is.EqualTo("ProjectWrite"));
            Assert.That(permissions.DefaultApprovalMode, Is.EqualTo(SessionApprovalMode.HumanApproval));
            Assert.That(config.Settings!.SessionDirectory, Is.EqualTo("logs"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestCase("""{"defaultMode":null}""")]
    [TestCase("""{"composerModes":null}""")]
    public async Task NullLegacyField_LoadsAsPendingMigrationWithLegacyDefaults(string permissionsJson)
    {
        var dir  = NewConfigDir();
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            await WriteRawAsync(path, "{\"settings\":{\"permissions\":" + permissionsJson + "}}");

            var permissions = (await UserConfigStore.LoadAsync(path, CancellationToken.None)).Settings!.Permissions!;
            Assert.That(permissions.PendingMigration, Is.True);
            Assert.That(permissions.LegacyDefaultMode, Is.EqualTo("ReadOnly"));
            Assert.That(permissions.LegacyComposerModes, Is.EqualTo(LegacyPermissionSelection.KnownLegacyModeNames));
            Assert.That(permissions.DefaultApprovalMode, Is.EqualTo(SessionApprovalMode.HumanApproval));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    [TestCase("ConfirmEveryAction", SessionApprovalMode.HumanApproval)]
    [TestCase("ReadOnly", SessionApprovalMode.HumanApproval)]
    [TestCase("ProjectWrite", SessionApprovalMode.HumanApproval)]
    [TestCase("FullAccess", SessionApprovalMode.HumanApproval)]
    [TestCase("ReviewEveryAction", SessionApprovalMode.AIAutoReview)]
    [TestCase("ReadOnlyAutoReview", SessionApprovalMode.AIAutoReview)]
    [TestCase("ProjectWriteAutoReview", SessionApprovalMode.AIAutoReview)]
    public async Task LegacyDefault_AllSevenModesMapExplicitly(string              legacyMode,
                                                               SessionApprovalMode expected)
    {
        var dir  = NewConfigDir();
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            var json = "{\"settings\":{\"permissions\":{\"defaultMode\":\"" + legacyMode +
                       "\",\"composerModes\":[\""                           + legacyMode + "\"]}}}";
            await WriteRawAsync(path, json);

            var config = await UserConfigStore.LoadAsync(path, CancellationToken.None);

            var permissions = config.Settings!.Permissions!;
            Assert.That(permissions.PendingMigration, Is.True);
            Assert.That(permissions.DefaultApprovalMode, Is.EqualTo(expected));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task LegacyDefault_MapsReviewPresetsToAIAutoReviewAndOthersToHuman()
    {
        var dir  = NewConfigDir();
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            await WriteRawAsync(path,
                                """{"settings":{"permissions":{"defaultMode":"ProjectWriteAutoReview","composerModes":["ProjectWriteAutoReview"]}}}""");
            var review = (await UserConfigStore.LoadAsync(path, CancellationToken.None)).Settings!.Permissions!;
            Assert.That(review.DefaultApprovalMode, Is.EqualTo(SessionApprovalMode.AIAutoReview));
            Assert.That(review.PendingMigration, Is.True);

            await WriteRawAsync(path,
                                """{"settings":{"permissions":{"defaultMode":"FullAccess","composerModes":["FullAccess"]}}}""");
            var fullAccess = (await UserConfigStore.LoadAsync(path, CancellationToken.None)).Settings!.Permissions!;
            Assert.That(fullAccess.DefaultApprovalMode, Is.EqualTo(SessionApprovalMode.HumanApproval));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task SavingPendingMigration_PreservesLegacyShapeVerbatim()
    {
        var dir  = NewConfigDir();
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            await WriteRawAsync(path,
                                """{"settings":{"permissions":{"defaultMode":"ReadOnly","composerModes":["ReadOnly"]}}}""");
            var loaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);

            await UserConfigStore.SaveAsync(path, loaded, CancellationToken.None);
            var json = JsonNode.Parse(await ReadConfigAsync(path))!.AsObject();

            var permissions = json["settings"]!["permissions"]!.AsObject();
            Assert.That(permissions.ContainsKey("defaultMode"), Is.True);
            Assert.That(permissions.ContainsKey("composerModes"), Is.True);
            Assert.That(permissions.ContainsKey("defaultApprovalMode"), Is.False);

            var reloaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);
            Assert.That(reloaded.Settings!.Permissions!.PendingMigration, Is.True);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task ConfirmingMigration_RewritesToCurrentShapeAndReloadsClean()
    {
        var dir  = NewConfigDir();
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            await WriteRawAsync(path,
                                """{"settings":{"permissions":{"defaultMode":"ReadOnlyAutoReview","composerModes":["ReadOnlyAutoReview"]}}}""");
            var pending = await UserConfigStore.LoadAsync(path, CancellationToken.None);
            Assert.That(pending.Settings!.Permissions!.DefaultApprovalMode,
                        Is.EqualTo(SessionApprovalMode.AIAutoReview));

            // 迁移确认：新形状实例（不携带旧字段），保存后旧字段从文件中消失。
            var confirmed = new UserConfig
            {
                Settings = new UserConfigSettings
                {
                    Permissions = new PermissionSettings
                    {
                        DefaultApprovalMode = pending.Settings.Permissions.DefaultApprovalMode,
                        UseCurrentModel     = true
                    }
                }
            };
            await UserConfigStore.SaveAsync(path, confirmed, CancellationToken.None);
            var json = JsonNode.Parse(await ReadConfigAsync(path))!.AsObject();

            var permissions = json["settings"]!["permissions"]!.AsObject();
            Assert.That(permissions["defaultApprovalMode"]!.GetValue<string>(), Is.EqualTo("AIAutoReview"));
            Assert.That(permissions.ContainsKey("defaultMode"), Is.False);
            Assert.That(permissions.ContainsKey("composerModes"), Is.False);

            var reloaded = await UserConfigStore.LoadAsync(path, CancellationToken.None);
            var settings = reloaded.Settings!.Permissions!;
            Assert.That(settings.PendingMigration, Is.False);
            Assert.That(settings.LegacyDefaultMode, Is.Null);
            Assert.That(settings.LegacyComposerModes, Is.Null);
            Assert.That(settings.DefaultApprovalMode, Is.EqualTo(SessionApprovalMode.AIAutoReview));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task MixedLegacyAndCurrentFields_AreRejected()
    {
        var dir  = NewConfigDir();
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            await WriteRawAsync(path,
                                """{"settings":{"permissions":{"defaultMode":"ReadOnly","defaultApprovalMode":"HumanApproval"}}}""");

            var ex =
                await TestAssert.ThrowsAsync<InvalidDataException>(() =>
                                                                       UserConfigStore
                                                                          .LoadAsync(path, CancellationToken.None));
            Assert.That(ex.Message, Does.Contain("mix"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestCase("""{"defaultMode":"ReadOnly","defaultApprovalMode":null}""")]
    [TestCase("""{"composerModes":["ReadOnly"],"defaultApprovalMode":null}""")]
    [TestCase("""{"defaultMode":null,"defaultApprovalMode":null}""")]
    [TestCase("""{"composerModes":null,"defaultApprovalMode":null}""")]
    public async Task LegacyFieldWithNullCurrentField_IsRejected(string permissionsJson)
    {
        var dir  = NewConfigDir();
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            await WriteRawAsync(path, "{\"settings\":{\"permissions\":" + permissionsJson + "}}");

            var exception =
                await TestAssert.ThrowsAsync<InvalidDataException>(() =>
                                                                       UserConfigStore
                                                                          .LoadAsync(path, CancellationToken.None));
            Assert.That(exception.Message, Does.Contain("mix"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task UnknownApprovalModeValue_IsRejected()
    {
        var dir  = NewConfigDir();
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            await WriteRawAsync(path,
                                """{"settings":{"permissions":{"defaultApprovalMode":"Aggressive"}}}""");

            await TestAssert.ThrowsAsync<InvalidDataException>(() =>
                                                                   UserConfigStore
                                                                      .LoadAsync(path, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task CurrentShapeWithoutApprovalMode_DefaultsToHumanApproval()
    {
        var dir  = NewConfigDir();
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            await WriteRawAsync(path, """{"settings":{"permissions":{"useCurrentModel":true}}}""");

            var config = await UserConfigStore.LoadAsync(path, CancellationToken.None);

            var settings = config.Settings!.Permissions!;
            Assert.That(settings.PendingMigration, Is.False);
            Assert.That(settings.DefaultApprovalMode, Is.EqualTo(SessionApprovalMode.HumanApproval));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public async Task CurrentShape_FixedReviewerRuleStillEnforced()
    {
        var dir  = NewConfigDir();
        var path = Path.Combine(dir, "user-config.json");
        try
        {
            var config = new UserConfig
            {
                Settings = new UserConfigSettings
                {
                    Permissions = new PermissionSettings
                    {
                        DefaultApprovalMode = SessionApprovalMode.AIAutoReview, UseCurrentModel = false
                    }
                }
            };

            await TestAssert.ThrowsAsync<InvalidDataException>(() => UserConfigStore.SaveAsync(path, config,
                                                                   CancellationToken.None));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
