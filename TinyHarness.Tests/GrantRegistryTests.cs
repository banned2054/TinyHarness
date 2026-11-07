using System.Text.Json.Nodes;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Permissions;

namespace TinyHarness.Tests;

public class GrantRegistryTests
{
    private static ToolPreparation Prep(string capability, IReadOnlyList<string> targets,
                                        string? constraint = null, string? grantConstraint = null,
                                        string? policyIdentity = null) => new()
    {
        ToolName               = "apply_patch",
        CallId                 = "call_1",
        Arguments              = new JsonObject { ["x"] = 1 },
        Capability             = capability,
        Summary                = "summary",
        SessionConstraint      = constraint,
        SessionGrantConstraint = grantConstraint,
        ExecutionPolicy        = policyIdentity,
        TargetPaths            = targets
    };

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan time) => _now += time;

        public override DateTimeOffset GetUtcNow() => _now;
    }

    [Test]
    public void Add_Peek_Consume_SingleOperation_IsSpentExactlyOnce()
    {
        var registry = new GrantRegistry();
        var write    = Prep("filesystem.write", [@"C:\ws\a.cs"]);

        Assert.That(registry.Peek(write), Is.Null);
        var grant = registry.Add(GrantScope.SingleOperation, write);

        Assert.Multiple(() =>
        {
            Assert.That(registry.Peek(write)!.GrantId, Is.EqualTo(grant.GrantId));
            Assert.That(registry.Consume(write)!.GrantId, Is.EqualTo(grant.GrantId));
            Assert.That(registry.Consume(write), Is.Null);
            Assert.That(registry.Peek(write), Is.Null);
            Assert.That(registry.SnapshotGrants().Single().State, Is.EqualTo(GrantState.Consumed));
        });
    }

    [Test]
    public void TurnAndSessionGrants_AreReturnedForReuse_WithoutConsumption()
    {
        var registry = new GrantRegistry();
        var write    = Prep("filesystem.write", [@"C:\ws\a.cs"]);

        foreach (var scope in new[] { GrantScope.Turn, GrantScope.Session })
        {
            registry.Add(scope, write);
            Assert.Multiple(() =>
            {
                Assert.That(registry.Consume(write), Is.Not.Null, scope.ToString());
                Assert.That(registry.Consume(write), Is.Not.Null, $"{scope} 授权应可复用。");
            });
        }
    }

    [Test]
    public void Matching_RequiresCapabilityConstraintPolicyIdentity_AndTargetCoverage()
    {
        var registry = new GrantRegistry();
        var granted  = Prep("process.execute", [@"C:\ws"], constraint : "command-a",
                            grantConstraint : "command-a\nexecution-policy: p1", policyIdentity : "p1");
        registry.Add(GrantScope.Turn, granted);

        Assert.Multiple(() =>
        {
            Assert.That(registry.Peek(Prep("process.execute", [@"C:\ws"], constraint : "command-a",
                                           grantConstraint : "command-a\nexecution-policy: p1",
                                           policyIdentity : "p1")), Is.Not.Null);
            Assert.That(registry.Peek(Prep("process.execute", [@"C:\ws"], constraint : "command-b",
                                           grantConstraint : "command-b\nexecution-policy: p1",
                                           policyIdentity : "p1")), Is.Null, "命令约束不一致不能命中。");
            Assert.That(registry.Peek(Prep("process.execute", [@"C:\ws"], constraint : "command-a",
                                           grantConstraint : "command-a\nexecution-policy: p2",
                                           policyIdentity : "p2")), Is.Null, "策略身份不一致不能命中。");
            Assert.That(registry.Peek(Prep("filesystem.write", [@"C:\ws\a.cs"])), Is.Null,
                        "能力不一致不能命中。");
            Assert.That(registry.Peek(Prep("process.execute", [@"C:\other"], constraint : "command-a",
                                           grantConstraint : "command-a\nexecution-policy: p1",
                                           policyIdentity : "p1")), Is.Null, "目标不在授权范围内不能命中。");
        });
    }

    [Test]
    public void DirectoryScope_CoversDescendants_AndGrantMustCoverEveryTarget()
    {
        var registry = new GrantRegistry();
        var scope    = Prep("filesystem.write", [@"C:\ws\src"]);
        registry.Add(GrantScope.Turn, scope);

        Assert.Multiple(() =>
        {
            Assert.That(registry.Peek(Prep("filesystem.write", [@"C:\ws\src\deep\a.cs"])), Is.Not.Null,
                        "目录范围覆盖后代路径。");
            Assert.That(registry.Peek(Prep("filesystem.write",
                                           [@"C:\ws\src\a.cs", @"C:\ws\src\deep\b.cs"])), Is.Not.Null,
                        "授权须覆盖全部目标时全部命中。");
            Assert.That(registry.Peek(Prep("filesystem.write",
                                           [@"C:\ws\src\a.cs", @"C:\ws\other\b.cs"])), Is.Null,
                        "任一目标脱靶即不命中。");
        });
    }

    [Test]
    public void Expiry_IsEvaluatedAgainstTheInjectedClock_AndLazilyMarksExpired()
    {
        var clock   = new FakeClock(new DateTimeOffset(2026, 11, 7, 0, 0, 0, TimeSpan.Zero));
        var registry = new GrantRegistry(clock);
        var write   = Prep("filesystem.write", [@"C:\ws\a.cs"]);
        var grant   = registry.Add(GrantScope.Turn, write, TimeSpan.FromMinutes(30));

        Assert.That(registry.Peek(write), Is.Not.Null);
        clock.Advance(TimeSpan.FromMinutes(31));

        Assert.Multiple(() =>
        {
            Assert.That(registry.Peek(write), Is.Null);
            Assert.That(registry.SnapshotGrants().Single(g => g.GrantId == grant.GrantId).State,
                        Is.EqualTo(GrantState.Expired));
        });
    }

    [Test]
    public void Revoke_RevokesById_OnlyWhileActive()
    {
        var registry = new GrantRegistry();
        var write    = Prep("filesystem.write", [@"C:\ws\a.cs"]);
        var grant    = registry.Add(GrantScope.Session, write);

        Assert.Multiple(() =>
        {
            Assert.That(registry.Revoke(grant.GrantId), Is.Not.Null);
            Assert.That(registry.Revoke(grant.GrantId), Is.Null, "已撤销的授权不能再次撤销。");
            Assert.That(registry.Peek(write), Is.Null);
            Assert.That(registry.SnapshotGrants().Single().State, Is.EqualTo(GrantState.Revoked));
        });
    }

    [Test]
    public void ExpireTurnGrants_KeepsSessionGrants_AndRevokeAllCoversBoth()
    {
        var registry = new GrantRegistry();
        var write    = Prep("filesystem.write", [@"C:\ws\a.cs"]);
        var turn     = registry.Add(GrantScope.Turn, write);
        var session  = registry.Add(GrantScope.Session, write);

        registry.ExpireTurnGrants();
        Assert.Multiple(() =>
        {
            Assert.That(registry.SnapshotGrants().Single(g => g.GrantId == turn.GrantId).State,
                        Is.EqualTo(GrantState.Expired));
            Assert.That(registry.SnapshotGrants().Single(g => g.GrantId == session.GrantId).State,
                        Is.EqualTo(GrantState.Active));
        });

        registry.RevokeAll();
        Assert.That(registry.SnapshotGrants().Single(g => g.GrantId == session.GrantId).State,
                    Is.EqualTo(GrantState.Revoked));
    }

    [Test]
    public void Add_SupersedesTheSameResourceAndScope_KeepingOtherScopes()
    {
        var registry = new GrantRegistry();
        var write    = Prep("filesystem.write", [@"C:\ws\a.cs"]);

        var first  = registry.Add(GrantScope.Turn, write);
        var single = registry.Add(GrantScope.SingleOperation, write);
        var second = registry.Add(GrantScope.Turn, write);

        var states = registry.SnapshotGrants().ToDictionary(grant => grant.GrantId, grant => grant.State);
        Assert.Multiple(() =>
        {
            Assert.That(states[first.GrantId], Is.EqualTo(GrantState.Superseded));
            Assert.That(states[second.GrantId], Is.EqualTo(GrantState.Active));
            Assert.That(states[single.GrantId], Is.EqualTo(GrantState.Active),
                        "不同范围的授权互不替代。");
        });
    }

    [Test]
    public void Events_AreDrainedInOccurrenceOrder_WithPreviousStates()
    {
        var registry = new GrantRegistry();
        var write    = Prep("filesystem.write", [@"C:\ws\a.cs"]);
        var grant    = registry.Add(GrantScope.Turn, write);
        registry.Revoke(grant.GrantId);

        var events = registry.DrainEvents();
        Assert.Multiple(() =>
        {
            Assert.That(events.Select(@event => @event.Action).ToArray(),
                        Is.EqualTo(new[] { "granted", "revoked" }));
            Assert.That(events[0].PreviousState, Is.EqualTo(GrantState.Active));
            Assert.That(events[1].PreviousState, Is.EqualTo(GrantState.Active));
            Assert.That(events[1].Grant.State, Is.EqualTo(GrantState.Revoked));
            Assert.That(registry.DrainEvents(), Is.Empty, "事件队列取空后不再重复。");
        });
    }

    [Test]
    public void Snapshot_AggregatesActiveGrantsByCapabilityGap()
    {
        var registry = new GrantRegistry();

        // 1) host 单次授权（host-execution 缺口）不污染快照：cwd 目标不属于文件系统范围。
        registry.Add(GrantScope.SingleOperation, Prep("process.execute", [@"C:\ws"], "h1"), "host-execution");

        // 2) additional-path-write Turn 授权聚合目标。
        registry.Add(GrantScope.Turn, Prep("filesystem.write", [@"C:\extra"], "w1"), "additional-path-write");

        // 3) metadata-write 聚合 MetadataWritePaths。
        registry.Add(GrantScope.Session, Prep("filesystem.write", [@"C:\ws\.git\config"], "m1"), "metadata-write");

        // 4) additional-path-read 聚合 AdditionalReadRoots。
        registry.Add(GrantScope.Turn, Prep("filesystem.read", [@"C:\extraead"], "r1"), "additional-path-read");

        var snapshot = registry.Snapshot();
        Assert.Multiple(() =>
        {
            Assert.That(snapshot.AdditionalWriteRoots, Is.EqualTo(new[] { @"C:\extra" }));
            Assert.That(snapshot.MetadataWritePaths, Is.EqualTo(new[] { @"C:\ws\.git\config" }));
            Assert.That(snapshot.AdditionalReadRoots, Is.EqualTo(new[] { @"C:\extraead" }));
            Assert.That(snapshot.AdditionalWriteRoots, Does.Not.Contain(@"C:\ws"),
                        "host 授权的 cwd 目标不得进入快照。");
        });

        // 5) 撤销后排除。
        var revoke = registry.Add(GrantScope.Turn, Prep("filesystem.write", [@"C:evoked"], "v1"),
                                  "additional-path-write");
        registry.Revoke(revoke.GrantId);
        Assert.That(registry.Snapshot().AdditionalWriteRoots, Does.Not.Contain(@"C:evoked"));
    }
}