using System.Text;
/* 临时验收文件（本轮新增，不提交）：以 TinyHarness.Tests 的 InternalsVisibleTo 通道调用
   生产规划代码 WindowsSandboxComposer.PlanBaselineReadProtectionTopLevelCore（internal），
   对仓库外隔离 deny 夹具根执行顶层读根规划。这不是 Desktop 生产组装路径：Desktop 生产
   入口的规划根恒为 UserProfile（真实 profile 验收另行安排）。 */
using NUnit.Framework;
using NUnit.Framework.Interfaces;
using NUnit.Framework.Internal;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         本机入口级验收：生产顶层读根规划代码 + 隔离 deny 夹具根的真实规划。入口与 Desktop
///         生产路径的差别仅在于规划根由环境变量注入，其余行为（只枚举直接子项一层、固定段
///         与顶层命中配对、真实文件系统枚举）均为生产行为；计划经 JSON 移交给
///         TinyHarness.Desktop.Tests 的组装路径测试。
///     </para>
///     <para>
///         Local entry-level acceptance: real planning by the production top-level read-root
///         planner against an isolated deny fixture root. The only difference from the Desktop
///         production path is the planning root, injected via environment variables; the rest
///         (one-level child enumeration only, fixed-segment and top-level-hit pairing, and the
///         real filesystem enumeration) is production behavior, and the plan is handed over via
///         JSON to the TinyHarness.Desktop.Tests assembly-path test.
///     </para>
/// </summary>
[TestFixture]
public sealed class BaselineScanIsolationAcceptanceTests
{
    private const string DenyRootEnv       = "TH_R5_DENY_FIXTURE_ROOT";
    private const string DenyPlanPathEnv   = "TH_R5_DENY_PLAN_PATH";
    private const string AcceptanceGateEnv = "TH_R5_ACCEPTANCE";

    /// <summary>
    ///     <para>
    ///         显式 opt-in 门禁：环境变量 TH_R5_ACCEPTANCE 精确等于 "1" 时用例才可运行；
    ///         未启用时在测试图构建阶段（先于夹具参数读取与任何副作用）标记为 Ignored。
    ///     </para>
    ///     <para>
    ///         Explicit opt-in gate: a case runs only when the environment variable
    ///         TH_R5_ACCEPTANCE equals exactly "1"; otherwise it is marked ignored at test-graph
    ///         build time, before any fixture configuration is read and before any side effect.
    ///     </para>
    /// </summary>
    private sealed class AcceptanceOptInAttribute : NUnitAttribute, IApplyToTest
    {
        private readonly string? _skipReason;

        public AcceptanceOptInAttribute()
        {
            // 构造阶段只读门禁；仅精确 "1" 激活，单独设置 TH_R5_* 夹具参数不激活。
            var gate = Environment.GetEnvironmentVariable(AcceptanceGateEnv);
            if (!string.Equals(gate, "1", StringComparison.Ordinal))
                _skipReason = "默认关闭的真实隔离规划验收：设置环境变量 TH_R5_ACCEPTANCE=1 显式启用。";
        }

        public void ApplyToTest(Test test)
        {
            // 与 NUnit IgnoreAttribute 一致：NotRunnable 优先级更高，不覆盖既有状态。
            if (_skipReason is null || test.RunState == RunState.NotRunnable) return;
            test.RunState = RunState.Ignored;
            test.Properties.Set(PropertyNames.SkipReason, _skipReason);
        }
    }

    /// <summary>
    ///     <para>
    ///         计划产出：对 deny 夹具根用生产顶层规划代码
    ///         （PlanBaselineReadProtectionTopLevelCore）真实规划，计划经 JSON 移交给
    ///         TinyHarness.Desktop.Tests 的组装路径测试。TinyHarness.Core 的
    ///         InternalsVisibleTo 只授予 TinyHarness.Tests，Desktop 测试无法直达 internal
    ///         规划入口，故以文件移交；计划语义（ReadRoots/DenyReadPaths）逐位置还原，
    ///         不改变任何条目。
    ///     </para>
    ///     <para>
    ///         Produces the plan: the deny fixture root is really planned by the production
    ///         top-level planner (PlanBaselineReadProtectionTopLevelCore), and the plan is handed
    ///         over via JSON to the TinyHarness.Desktop.Tests assembly-path test. TinyHarness.Core's
    ///         InternalsVisibleTo only grants TinyHarness.Tests, so the Desktop test cannot reach
    ///         the internal planning entry directly; the handover is positional (ReadRoots and
    ///         DenyReadPaths) and changes no entry.
    ///     </para>
    /// </summary>
    [Test]
    [AcceptanceOptIn]
    public void EmitDenyFixturePlan_ProductionTopLevelPlanner()
    {
        var denyRoot = Environment.GetEnvironmentVariable(DenyRootEnv);
        var planPath = Environment.GetEnvironmentVariable(DenyPlanPathEnv);
        Assert.That(denyRoot, Is.Not.Null.And.Not.Empty, $"missing env {DenyRootEnv}");
        Assert.That(planPath, Is.Not.Null.And.Not.Empty, $"missing env {DenyPlanPathEnv}");
        Assert.That(Directory.Exists(denyRoot!), Is.True, "the deny fixture root must exist");

        var plan = WindowsSandboxComposer.PlanBaselineReadProtectionTopLevelCore(denyRoot!, [], null, null,
                                                                                 CancellationToken.None);

        var document = System.Text.Json.JsonSerializer.Serialize(new
        {
            ReadRoots     = plan.ReadRoots,
            DenyReadPaths = plan.DenyReadPaths
        });
        File.WriteAllText(planPath!, document, new UTF8Encoding(false));

        var report = new StringBuilder();
        report.Append("timestampUtc=").AppendLine(DateTime.UtcNow.ToString("o"));
        report.Append("denyFixtureRoot=").AppendLine(Path.GetFullPath(denyRoot!));
        report.Append("readRoots=").AppendLine(string.Join(";", plan.ReadRoots));
        report.Append("denyCount=").AppendLine(plan.DenyReadPaths.Count.ToString());
        foreach (var deny in plan.DenyReadPaths) report.Append("denyPath=").AppendLine(deny);
        report.Append("planHandoverPath=").AppendLine(planPath!);
        File.WriteAllText(planPath! + ".report.txt", report.ToString(), new UTF8Encoding(false));
        TestContext.Out.Write(report.ToString());

        Assert.Multiple(() =>
        {
            // 顶层读根=普通顶层子项（normal.txt 与 keys）；夹具根本身不再是读根。
            Assert.That(plan.ReadRoots, Has.Count.EqualTo(2), "normal.txt and keys only");
            Assert.That(plan.ReadRoots.Any(p => string.Equals(p, Path.GetFullPath(denyRoot!),
                                                              StringComparison.OrdinalIgnoreCase)),
                        Is.False, "the fixture root itself is no longer a read root");
            Assert.That(plan.ReadRoots.Any(p => p.EndsWith("\\normal.txt", StringComparison.OrdinalIgnoreCase)),
                        Is.True, "normal.txt is a read root");
            Assert.That(plan.ReadRoots.Any(p => p.EndsWith("\\keys", StringComparison.OrdinalIgnoreCase)),
                        Is.True, "keys is a read root");

            // 拒读集：6 条基线（5 个顶层敏感目录段 + .codex）+ 顶层 .env 命中 = 7。
            // keys\server.pem 在普通读根内任意深度，首版 OS 语义收敛后不由启动规划发现；
            // credential.txt 被 .aws 整目录基线条目覆盖，不重复入集；正对照不入集。
            Assert.That(plan.DenyReadPaths, Has.Count.EqualTo(7), "baseline segments plus the top-level .env hit");
            Assert.That(plan.DenyReadPaths.Any(p => p.EndsWith("\\.env", StringComparison.OrdinalIgnoreCase)),
                        Is.True, "the top-level .env pairs");
            Assert.That(plan.DenyReadPaths.Any(p => p.EndsWith("\\.aws", StringComparison.OrdinalIgnoreCase)),
                        Is.True, "the fixed .aws segment pairs");
            Assert.That(plan.DenyReadPaths.Any(p => p.EndsWith("server.pem", StringComparison.OrdinalIgnoreCase)),
                        Is.False, "nested server.pem is not discovered by the top-level planning");
            Assert.That(plan.DenyReadPaths.Any(p => p.Contains("\\.aws\\credential.txt",
                                                                     StringComparison.OrdinalIgnoreCase)),
                        Is.False, "credential.txt is covered by the .aws deny ancestor");
            Assert.That(plan.DenyReadPaths.Any(p => p.EndsWith("\\normal.txt", StringComparison.OrdinalIgnoreCase)) ||
                        plan.DenyReadPaths.Any(p => p.EndsWith("\\readme.txt", StringComparison.OrdinalIgnoreCase)),
                        Is.False, "positive controls stay out of the deny set");
        });
    }
}
