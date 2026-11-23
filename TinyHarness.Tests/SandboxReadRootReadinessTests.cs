using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         组读根就绪门（<see cref="SandboxReadRootReadiness" />）的定向断言：沙箱组 Allow 读
///         ACE 在位即过、内置读在位（helper skip 形态，含经父目录继承的实例）即过、先缺后现
///         （延迟授予）经轮询放行、超预算 fail-closed 并列出未就绪读根与阶段、缺失路径视为
///         未就绪。全部使用测试自有临时目录的真实 DACL，不触碰机器共享状态。
///     </para>
///     <para>
///         Targeted assertions for the group read-root readiness gate
///         (<see cref="SandboxReadRootReadiness" />): passes with a sandbox-group Allow read ACE,
///         passes with built-in read (the helper-skip shape, including inherited from a parent),
///         polls to release when the ACE appears late, fails closed at budget exhaustion listing
///         the unready roots and the stage, and treats a missing path as unready. Everything uses
///         real DACLs on test-owned temp directories without touching shared machine state.
///     </para>
/// </summary>
public class SandboxReadRootReadinessTests
{
    private static readonly TimeSpan ShortBudget = TimeSpan.FromMilliseconds(5_000);
    private static readonly TimeSpan ShortPoll  = TimeSpan.FromMilliseconds(50);

    private static SandboxSetupPayload Payload(params string[] readRoots) => new()
    {
        OfflineUsername         = "TinyHarnessOffline",
        OnlineUsername          = "TinyHarnessOnline",
        SandboxHome             = readRoots[0],
        CommandWorkingDirectory = readRoots[0],
        ReadRoots               = readRoots,
        WriteRoots              = [],
        RealUser                = "real"
    };

    [SupportedOSPlatform("windows")]
    private static void GrantAllowRead(string directory, IdentityReference identity)
    {
        var security = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.ReadAndExecute,
                                                        InheritanceFlags.ContainerInherit |
                                                        InheritanceFlags.ObjectInherit,
                                                        PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    [Test]
    public async Task Validate_SandboxGroupAllowReadAce_Passes()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Read-root readiness needs Windows ACLs.");
            return;
        }
        SecurityIdentifier group;
        try
        {
            group = (SecurityIdentifier)new NTAccount(Environment.MachineName, "TinyHarnessUsers")
                   .Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        {
            Assert.Ignore("The TinyHarnessUsers group does not exist on this machine.");
            return;
        }

        using var root = new TestTempDir();
        var readRoot = root.CreateDirectory("read-root");
        GrantAllowRead(readRoot, group);

        await SandboxReadRootReadiness.ValidateAsync(Payload(readRoot), ShortBudget, ShortPoll,
                                                     CancellationToken.None);
    }

    [Test]
    public async Task Validate_BuiltInUsersAllowRead_Passes()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Read-root readiness needs Windows ACLs.");
            return;
        }

        // helper 对已有内置读跳过授予的形态：BUILTIN\Users 的 Allow 读 ACE（此处经父目录继承
        // 落到读根）即视为就绪，不要求沙箱组 ACE 在位。
        using var root   = new TestTempDir();
        var       parent = root.CreateDirectory("read-parent");
        GrantAllowRead(parent, new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null));
        var readRoot = root.CreateDirectory("read-parent/read-root");

        await SandboxReadRootReadiness.ValidateAsync(Payload(readRoot), ShortBudget, ShortPoll,
                                                     CancellationToken.None);
    }

    [Test]
    public async Task Validate_AceAppearsLate_PollsThenReleases()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Read-root readiness needs Windows ACLs.");
            return;
        }

        using var root = new TestTempDir();
        var readRoot = root.CreateDirectory("read-root");
        var granted  = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            // 延迟授予体只在 Windows 测试路径运行；平台守卫同时满足 ACL 调用的分析要求。
            if (!OperatingSystem.IsWindows()) return;

            await Task.Delay(300);
            GrantAllowRead(readRoot, new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null));
            granted.TrySetResult(true);
        });

        // 冷启动竞态形态：refresh 返回时授予尚未落地，门按间隔轮询至 ACE 出现后放行。
        await SandboxReadRootReadiness.ValidateAsync(Payload(readRoot), ShortBudget, ShortPoll,
                                                     CancellationToken.None);
        Assert.That(await granted.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
    }

    [Test]
    public async Task Validate_BudgetExceeded_FailsClosedListingUnreadyRoots()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Ignore("Read-root readiness needs Windows ACLs.");
            return;
        }

        using var root = new TestTempDir();
        var missingGrant = root.CreateDirectory("no-grant");
        var vanished     = Path.Combine(root.Root, "never-existed");

        var error = await TestAssert.ThrowsAsync<ProcessExecutionStartException>(() =>
            SandboxReadRootReadiness.ValidateAsync(Payload(missingGrant, vanished),
                                                   TimeSpan.FromMilliseconds(400), ShortPoll,
                                                   CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(error.Message, Does.Contain("read-root readiness"));
            Assert.That(error.Message, Does.Contain(missingGrant));
            Assert.That(error.Message, Does.Contain(vanished));
            Assert.That(error.InnerException, Is.TypeOf<TimeoutException>());
        });
    }
}
