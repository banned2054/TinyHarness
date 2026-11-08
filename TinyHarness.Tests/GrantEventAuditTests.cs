using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         permission-grant-event/1 必需记录矩阵：严格校验（schema/事件/范围/授权变更字段约束）、
///         六种事件的接受、畸形记录的拒绝、source-gen 往返保真、SessionLogStore 控制记录接受与
///         fail-closed 拒绝（未知 kind 即旧程序拒读路径）、GrantEventAudit 映射（superseded 归
///         invalidated、未知动作 fail-closed、脱敏函数作用于 targets）。恢复不重建可执行授权。
///     </para>
///     <para>
///         The permission-grant-event/1 required-record matrix: strict validation (schema, events, scopes,
///         authorization-changed field constraints), acceptance of all six events, rejection of malformed
///         records, source-gen round-trip fidelity, SessionLogStore control-record acceptance and fail-closed
///         rejection (an unknown kind is exactly the old-reader rejection path), and the GrantEventAudit
///         mapping (superseded lands as invalidated, unknown actions fail closed, and the redaction function
///         applies to targets). Recovery never rebuilds executable authority.
///     </para>
/// </summary>
public class GrantEventAuditTests
{
    private static PermissionGrantEventRecord Lifecycle(
        string grantId = "grant-1", string @event = "granted", string scope = "turn",
        string capability = "filesystem.write", string revision = "backend=windows-sandbox",
        string? reason = null) => new()
    {
        Schema = PermissionGrantEventRecord.CurrentSchema,
        GrantId = grantId,
        Event = @event,
        Scope = scope,
        Capability = capability,
        Targets = [@"C:\outside\target.txt"],
        PolicyIdentityRevision = revision,
        Reason = reason,
        Utc = DateTimeOffset.Parse("2026-11-08T01:02:03.4567890+00:00", CultureInfo.InvariantCulture)
    };

    private static PermissionGrantEventRecord AuthorizationChanged(string reason = "permission migration confirmed") =>
        new()
        {
            Schema = PermissionGrantEventRecord.CurrentSchema,
            GrantId = string.Empty,
            Event = "authorization-changed",
            Scope = "none",
            Capability = "authorization",
            Targets = [],
            PolicyIdentityRevision = string.Empty,
            Reason = reason,
            Utc = DateTimeOffset.Parse("2026-11-08T01:02:03.4567890+00:00", CultureInfo.InvariantCulture)
        };

    // ---- 接受：六种事件 ----

    [TestCase("granted")]
    [TestCase("consumed")]
    [TestCase("revoked")]
    [TestCase("expired")]
    [TestCase("invalidated")]
    [TestCase("authorization-changed")]
    public void Record_AcceptsEveryKnownEvent(string @event)
    {
        var record = @event == "authorization-changed"
            ? AuthorizationChanged()
            : Lifecycle(@event : @event, scope : @event == "consumed" ? "single-operation" : "turn");

        Assert.DoesNotThrow(record.Validate);
    }

    // ---- 拒绝矩阵 ----

    [Test]
    public void Record_RejectsForeignSchema() =>
        Assert.Throws<InvalidDataException>(() => (Lifecycle() with
        {
            Schema = "tinyharness.permission-grant-event/0"
        }).Validate());

    [Test]
    public void Record_RejectsUnknownEvent() =>
        Assert.Throws<InvalidDataException>(() => Lifecycle(@event : "exploded").Validate());

    [Test]
    public void Record_RejectsMissingCapability() =>
        Assert.Throws<InvalidDataException>(() => Lifecycle(capability : " ").Validate());

    [Test]
    public void Record_RejectsLifecycleEventWithoutGrantId() =>
        Assert.Throws<InvalidDataException>(() => Lifecycle(grantId : "").Validate());

    [Test]
    public void Record_RejectsLifecycleEventWithoutPolicyRevision() =>
        Assert.Throws<InvalidDataException>(() => Lifecycle(revision : "").Validate());

    [TestCase("weird")]
    [TestCase("none")]
    public void Record_RejectsUnknownLifecycleScope(string scope) =>
        Assert.Throws<InvalidDataException>(() => Lifecycle(scope : scope).Validate());

    [Test]
    public void Record_RejectsAuthorizationChangedCarryingGrantIdentity() =>
        Assert.Throws<InvalidDataException>(() => (AuthorizationChanged() with { GrantId = "grant-1" }).Validate());

    [Test]
    public void Record_RejectsAuthorizationChangedWithLifecycleScope() =>
        Assert.Throws<InvalidDataException>(() => (AuthorizationChanged() with { Scope = "turn" }).Validate());

    [Test]
    public void Record_RejectsAuthorizationChangedCarryingTargets() =>
        Assert.Throws<InvalidDataException>(() => (AuthorizationChanged() with
        {
            Targets = [@"C:\x"]
        }).Validate());

    [Test]
    public void Record_RejectsAuthorizationChangedWithoutReason() =>
        Assert.Throws<InvalidDataException>(() => AuthorizationChanged(reason : "").Validate());

    // ---- 往返保真 ----

    [Test]
    public void Record_RoundTripsThroughThePersistenceContext()
    {
        var original = Lifecycle(reason : null) with { Reason = null };
        var json = JsonSerializer.Serialize(original, PermissionPersistenceJsonContext.Default
                                                                    .PermissionGrantEventRecord);
        var restored = JsonSerializer.Deserialize(json, PermissionPersistenceJsonContext.Default
                                                                       .PermissionGrantEventRecord)!;

        Assert.Multiple(() =>
        {
            Assert.That(restored.Schema, Is.EqualTo(original.Schema));
            Assert.That(restored.GrantId, Is.EqualTo(original.GrantId));
            Assert.That(restored.Event, Is.EqualTo(original.Event));
            Assert.That(restored.Scope, Is.EqualTo(original.Scope));
            Assert.That(restored.Capability, Is.EqualTo(original.Capability));
            Assert.That(restored.Targets, Is.EqualTo(original.Targets));
            Assert.That(restored.PolicyIdentityRevision, Is.EqualTo(original.PolicyIdentityRevision));
            Assert.That(restored.Reason, Is.Null);
            Assert.That(restored.Utc, Is.EqualTo(original.Utc));
            Assert.DoesNotThrow(restored.Validate);
        });
    }

    // ---- SessionLogStore 控制记录面 ----

    [Test]
    public void Store_AcceptsValidGrantEventControlRecord()
    {
        var record = new SessionLogRecord(string.Empty, "permission-grant-event",
                                          JsonSerializer.SerializeToElement(Lifecycle(),
                                                                            PermissionPersistenceJsonContext
                                                                               .Default
                                                                               .PermissionGrantEventRecord));
        Assert.DoesNotThrow(() => SessionStoreWriter.ValidateControlRecord(record));
    }

    [TestCase("""{"schema":"tinyharness.permission-grant-event/1","grantId":"g","event":"exploded","scope":"turn","capability":"filesystem.write","targets":[],"policyIdentityRevision":"x","utc":"2026-11-08T01:02:03+00:00"}""")]
    [TestCase("""{"schema":"tinyharness.permission-grant-event/0","grantId":"g","event":"granted","scope":"turn","capability":"filesystem.write","targets":[],"policyIdentityRevision":"x","utc":"2026-11-08T01:02:03+00:00"}""")]
    [TestCase("""{"schema":"tinyharness.permission-grant-event/1","grantId":"","event":"authorization-changed","scope":"none","capability":"authorization","targets":[],"policyIdentityRevision":"","utc":"2026-11-08T01:02:03+00:00"}""")]
    public void Store_RejectsMalformedGrantEventControlRecords(string payload)
    {
        var record = new SessionLogRecord(string.Empty, "permission-grant-event",
                                          JsonDocument.Parse(payload).RootElement);

        Assert.Throws<InvalidDataException>(() => SessionStoreWriter.ValidateControlRecord(record));
    }

    [Test]
    public void Store_RejectsUnknownControlKinds_FailClosedLikeAnOldReader()
    {
        var record = new SessionLogRecord(string.Empty, "permission-grant-event/9",
                                          JsonDocument.Parse("{}").RootElement);

        // 旧程序不认识新 kind 时走同一条 default fail-closed 路径（必需记录语义）。
        var error = Assert.Throws<SessionStoreException>(() => SessionStoreWriter.ValidateControlRecord(record));
        Assert.That(error!.Message, Does.Contain("Unknown control record kind"));
    }

    [Test]
    public async Task StoreWithGrantEventRecords_RestoresWithoutRebuildingExecutableAuthority()
    {
        using var workspace = new TestTempDir();
        using var store = new TestTempDir();
        var session = new ChatSession(Guid.NewGuid().ToString("N"), workspace.Root,
                                      new SessionModelIdentity("test-model", "https://example.invalid/v1",
                                                               ChatApiKind.ChatCompletions));
        var context = new ConversationContext();
        context.Append(ChatMessage.System("system prompt"));
        var writer = new SessionStoreWriter(store.Root, session.Id);
        try
        {
            await writer.CommitControlAsync(session, context,
                                            [
                                                new SessionLogRecord(string.Empty, "permission-grant-event",
                                                                     JsonSerializer.SerializeToElement(Lifecycle(),
                                                                         PermissionPersistenceJsonContext.Default
                                                                            .PermissionGrantEventRecord)),
                                                new SessionLogRecord(string.Empty, "permission-grant-event",
                                                                     JsonSerializer.SerializeToElement(
                                                                         AuthorizationChanged(),
                                                                         PermissionPersistenceJsonContext.Default
                                                                            .PermissionGrantEventRecord))
                                            ], CancellationToken.None);

            // 新程序：接受必需记录并正常恢复；恢复不重建任何可执行授权（快照无授权状态字段可建）。
            var snapshot = await SessionStoreReader.ReadAsync(store.Root, session.Id, CancellationToken.None);
            Assert.That(snapshot, Is.Not.Null);
            var restored = SessionResume.Restore(snapshot!, workspace.Root, "system prompt");
            Assert.That(restored.Session.Id, Is.EqualTo(session.Id));
        }
        finally
        {
            writer.Dispose();
        }
    }

    // ---- GrantEventAudit 映射 ----

    [Test]
    public void FromEvent_MapsLifecycleEvents_WithRedactionAndRevision()
    {
        var grant = Grant.Active("grant-1", GrantScope.Turn, ShellPreparation(@"C:\secret\path"), FixedUtc,
                                 TimeSpan.FromHours(1)) with { State = GrantState.Active };
        var record = GrantEventAudit.FromEvent(
            new GrantRegistry.GrantEvent(grant, GrantState.Active, "granted", FixedUtc),
            path => path.Replace(@"C:\secret", @"C:\redacted"));

        Assert.Multiple(() =>
        {
            Assert.That(record.Schema, Is.EqualTo(PermissionGrantEventRecord.CurrentSchema));
            Assert.That(record.Event, Is.EqualTo("granted"));
            Assert.That(record.Scope, Is.EqualTo("turn"));
            Assert.That(record.Capability, Is.EqualTo(grant.Capability));
            Assert.That(record.Targets, Is.EqualTo(new[] { @"C:\redacted\path" }), "脱敏函数作用于 targets。");
            Assert.That(record.PolicyIdentityRevision, Is.EqualTo(grant.PolicyIdentity));
            Assert.That(record.Utc, Is.EqualTo(FixedUtc));
        });
    }

    [TestCase("superseded", "invalidated")]
    [TestCase("consumed", "consumed")]
    [TestCase("revoked", "revoked")]
    [TestCase("expired", "expired")]
    public void FromEvent_MapsActionsToStableEventNames(string action, string expectedEvent)
    {
        var grant = Grant.Active("grant-1", GrantScope.Turn, ShellPreparation(@"C:\outside\target"), FixedUtc);
        var record = GrantEventAudit.FromEvent(
            new GrantRegistry.GrantEvent(grant, GrantState.Active, action, FixedUtc), static path => path);

        Assert.That(record.Event, Is.EqualTo(expectedEvent));
    }

    [Test]
    public void FromEvent_FailsClosedOnUnknownAction()
    {
        var grant = Grant.Active("grant-1", GrantScope.Turn, ShellPreparation(@"C:\outside\target"), FixedUtc);

        Assert.Throws<InvalidOperationException>(() => GrantEventAudit.FromEvent(
            new GrantRegistry.GrantEvent(grant, GrantState.Active, "teleported", FixedUtc),
            static path => path));
    }

    [Test]
    public void AuthorizationChanged_CarriesNoGrantIdentityAndRedactsTheReason()
    {
        var record = GrantEventAudit.AuthorizationChanged("released by <secret>",
                                                          FixedUtc,
                                                          reason => reason.Replace("<secret>", "[redacted]"));

        Assert.Multiple(() =>
        {
            Assert.That(record.Event, Is.EqualTo("authorization-changed"));
            Assert.That(record.GrantId, Is.Empty);
            Assert.That(record.Scope, Is.EqualTo("none"));
            Assert.That(record.Capability, Is.EqualTo("authorization"));
            Assert.That(record.Targets, Is.Empty);
            Assert.That(record.PolicyIdentityRevision, Is.Empty);
            Assert.That(record.Reason, Is.EqualTo("released by [redacted]"));
        });
    }

    // ---- 工具 ----

    private static DateTimeOffset FixedUtc => DateTimeOffset.Parse("2026-11-08T01:02:03.4567890+00:00",
                                                                   CultureInfo.InvariantCulture);

    private static ToolPreparation ShellPreparation(string target) => new()
    {
        ToolName          = "shell",
        CallId            = "call_1",
        Arguments         = new JsonObject { ["mode"] = "direct" },
        Capability        = "process.execute",
        Summary           = "shell command",
        SessionConstraint = "cmd-identity",
        ExecutionPolicy   = "backend=windows-sandbox\npolicyVersion=6",
        TargetPaths       = [target]
    };
}
