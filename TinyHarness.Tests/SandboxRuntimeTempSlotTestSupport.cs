using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         槽位测试的共享夹具：固定合成 SID、宿主主体模板、临时 home 初始化、DACL 篡改与链接创建。所有成员只在
///         Windows 上调用，测试本身以 <c>OperatingSystem.IsWindows()</c> 守卫。
///     </para>
///     <para>
///         Shared fixtures for the slot tests: fixed synthetic SIDs, the host principal template, temporary home
///         initialization, DACL tampering, and link creation. Every member is called on Windows only and the tests
///         guard with <c>OperatingSystem.IsWindows()</c>.
///     </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SandboxRuntimeTempSlotTestSupport
{
    internal const string SandboxAccountSid       = "S-1-5-21-1-2-3-1000";
    internal const string SandboxSharedSid        = "S-1-5-21-1-2-3-1001";
    internal const string SlotCapabilitySid       = "S-1-5-21-1-2-3-2001";
    internal const string SecondSlotCapabilitySid = "S-1-5-21-1-2-3-2002";
    internal const string ForeignCapabilitySid    = "S-1-5-21-1-2-3-2003";
    internal const string ReadOnlyCapabilitySid   = "S-1-5-21-1-2-3-2004";

    internal static SandboxRuntimeTempSlotAclSpec CreateSpec(string? capabilitySid = null) =>
        SandboxRuntimeTempSlotInitializer.CreateHostTemplate(
            [new SecurityIdentifier(SandboxSharedSid)],
            [new SecurityIdentifier(SandboxAccountSid)],
            new SecurityIdentifier(capabilitySid ?? SlotCapabilitySid));

    internal static SandboxRuntimeTempSlot Initialize(
        TestTempDir home, int slotIndex = 0, string? capabilitySid = null) =>
        SandboxRuntimeTempSlotInitializer.Initialize(home.Root, slotIndex, CreateSpec(capabilitySid));

    internal static string HostUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("The test process has no user SID.");
    }

    internal static FileSystemAccessRule AllowRule(
        string           sid,
        FileSystemRights rights,
        InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
        PropagationFlags propagation = PropagationFlags.None) =>
        new(new SecurityIdentifier(sid), rights, inheritance, propagation, AccessControlType.Allow);

    internal static void Tamper(string directoryPath, Action<DirectorySecurity> change)
    {
        var info     = new DirectoryInfo(directoryPath);
        var security = info.GetAccessControl(AccessControlSections.Access);
        change(security);
        info.SetAccessControl(security);
    }

    internal static DirectorySecurity ReadAccess(string directoryPath) =>
        new DirectoryInfo(directoryPath).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);

    internal static void RemoveAllowRules(string directoryPath, string sid) =>
        Tamper(directoryPath, security =>
        {
            foreach (var rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier))
                                         .Cast<FileSystemAccessRule>()
                                         .Where(rule => rule.AccessControlType == AccessControlType.Allow &&
                                                        rule.IdentityReference.Value == sid)
                                         .ToArray())
                security.RemoveAccessRuleSpecific(rule);
        });

    internal static void RemoveDenyRules(string directoryPath, string sid) =>
        Tamper(directoryPath, security =>
        {
            foreach (var rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier))
                                         .Cast<FileSystemAccessRule>()
                                         .Where(rule => rule.AccessControlType == AccessControlType.Deny &&
                                                        rule.IdentityReference.Value == sid)
                                         .ToArray())
                security.RemoveAccessRuleSpecific(rule);
        });

    /// <summary>
    ///     <para>创建目录联接（junction）：不需要管理员权限，测试用它覆盖"重解析点不跟进"的行为。</para>
    ///     <para>
    ///         Creates a directory junction: it needs no administrator rights and lets the test cover "reparse
    ///         points are never followed" behavior.
    ///     </para>
    /// </summary>
    internal static bool TryCreateJunction(string linkPath, string targetPath) =>
        RunMklink("/J", linkPath, targetPath);

    /// <summary>
    ///     <para>创建文件硬链接；同一卷内不需要管理员权限。</para>
    ///     <para>Creates a file hard link; no administrator rights are needed within one volume.</para>
    /// </summary>
    internal static bool TryCreateHardLink(string linkPath, string targetPath) =>
        RunMklink("/H", linkPath, targetPath);

    private static bool RunMklink(string flag, string linkPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        {
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add(flag);
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);
        using var process = Process.Start(startInfo);
        if (process is null) return false;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        return process.WaitForExit(15_000) && process.ExitCode == 0;
    }
}
