using NUnit.Framework;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Desktop.Tests;

/// <summary>
///     <para>验证桌面授权入口在参数无效时拒绝启动，不进入窗口或离线 smoke。</para>
///     <para>Verifies that malformed desktop grant requests fail without starting windows or offline smoke.</para>
/// </summary>
public sealed class DesktopAclHelperEntryTests
{
    [Test]
    public void MissingChannel_IsRejectedBeforeDesktopStartup() =>
        Assert.That(Program.Main([ProcessWorkspaceAclGrant.HelperArgument]), Is.EqualTo(2));

    [Test]
    public void ExtraArguments_CannotRedirectHelperToOfflineSmoke() =>
        Assert.That(Program.Main([ProcessWorkspaceAclGrant.HelperArgument, "unused-channel", "--offline-smoke"]),
                    Is.EqualTo(2));
}
