#if DEBUG
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         验证 Debug 诊断日志的落盘、单行脱敏格式、失败静默与 ACL 差异描述；Release 构建不包含这些用例。
///     </para>
///     <para>
///         Verifies the Debug diagnostic log's persistence, single-line sanitized format, silent failure,
///         and ACL diff description; Release builds contain no such cases.
///     </para>
/// </summary>
public class SandboxDiagnosticLogTests
{
    [SetUp]
    public void ResetLogger() => SandboxDiagnosticLog.ResetForTests();

    [TearDown]
    public void ClearOverride() => SandboxDiagnosticLog.LogDirectoryOverrideForTests = null;

    [Test]
    public async Task EventAfterInitialize_WritesSanitizedSingleLinesWithHeader()
    {
        using var logs = new TestTempDir();
        SandboxDiagnosticLog.LogDirectoryOverrideForTests = logs.Root;
        SandboxDiagnosticLog.Initialize();
        SandboxDiagnosticLog.Event("unit", "first\nsecond");
        await SandboxDiagnosticLog.FlushForTestsAsync();
        var file  = Directory.GetFiles(logs.Root, "sandbox-diagnostic-*.log").Single();
        var lines = await File.ReadAllLinesAsync(file);
        Assert.That(lines.Any(line => line.Contains("|startup|") && line.Contains("build=Debug")), Is.True);
        Assert.That(lines.Any(line => line.Contains("|unit|")    && line.Contains(@"first\nsecond")), Is.True);
        Assert.That(lines.All(line => line.Contains('|')), Is.True,
                    "Raw newlines would split one event across multiple lines.");
    }

    [Test]
    public async Task UnusableLogDirectory_SilentlyDisablesLoggingWithoutThrowing()
    {
        var blockedPath = Path.Combine(Path.GetTempPath(), "tinyharness-log-blocked-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(blockedPath, string.Empty);
        try
        {
            using var usable = new TestTempDir();
            SandboxDiagnosticLog.LogDirectoryOverrideForTests = blockedPath;
            SandboxDiagnosticLog.Initialize();
            Assert.DoesNotThrow(() => SandboxDiagnosticLog.Event("unit", "dropped"));
            await SandboxDiagnosticLog.FlushForTestsAsync();
            SandboxDiagnosticLog.LogDirectoryOverrideForTests = usable.Root;
            SandboxDiagnosticLog.Initialize();
            SandboxDiagnosticLog.Event("unit", "still-dropped");
            await SandboxDiagnosticLog.FlushForTestsAsync();
            Assert.That(Directory.GetFiles(usable.Root), Is.Empty, "A disabled log must stay disabled.");
        }
        finally
        {
            File.Delete(blockedPath);
        }
    }

    [Test]
    public void DescribeUnsatisfiedParentRules_ReportsSidAndMaskDifference()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sid = new SecurityIdentifier("S-1-5-21-77-77-77-701");
        var parent = new FileSystemAccessRule(sid, FileSystemRights.Modify,
                                              InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                                              PropagationFlags.None, AccessControlType.Allow);
        var partial = new FileSystemAccessRule(sid, FileSystemRights.ReadAndExecute,
                                               InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                                               PropagationFlags.None, AccessControlType.Allow);
        var missing = WorkspaceAclInheritanceDiagnostics.DescribeUnsatisfiedParentRules([parent], [partial], true);
        Assert.That(missing, Does.Contain(sid.ToString()));
        Assert.That(missing, Does.Contain("required=0x"));
        Assert.That(missing, Does.Contain("effective=0x"));
        var satisfied = new FileSystemAccessRule(sid, FileSystemRights.Modify,
                                                 InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                                                 PropagationFlags.None, AccessControlType.Allow);
        Assert.That(WorkspaceAclInheritanceDiagnostics.DescribeUnsatisfiedParentRules([parent], [satisfied], true),
                    Is.EqualTo("<none>"));
    }

    [Test]
    public void DescribeRule_IncludesSidMaskAndFlags()
    {
        if (!OperatingSystem.IsWindows()) return;
        var sid  = new SecurityIdentifier("S-1-5-21-77-77-77-702");
        var rule = new FileSystemAccessRule(sid, FileSystemRights.Write, AccessControlType.Allow);
        var text = WorkspaceAclInheritanceDiagnostics.DescribeRule(rule);
        Assert.That(text, Does.Contain("sid=" + sid));
        Assert.That(text, Does.Contain("mask=0x"));
        Assert.That(text, Does.Contain("inherited=False"));
    }

    [Test]
    public async Task LogCapabilityContextAsync_ReportsCapSidEntriesForWriteRoots()
    {
        using var home      = new TestTempDir();
        using var workspace = new TestTempDir();
        using var logs      = new TestTempDir();
        var       key       = SandboxCapabilitySidStore.CanonicalRootKey(workspace.Root);
        var table = new SandboxCapabilitySids
        {
            Workspace          = "S-1-5-21-77-77-77-711",
            ReadOnly           = "S-1-5-21-77-77-77-712",
            WorkspaceByCwd     = new Dictionary<string, string> { [key] = "S-1-5-21-77-77-77-713" },
            WritableRootByPath = []
        };
        await File.WriteAllTextAsync(Path.Combine(home.Root, "cap_sid"),
                                     JsonSerializer.Serialize(table,
                                                              WindowsSandboxJsonContext.Default.SandboxCapabilitySids));
        SandboxDiagnosticLog.LogDirectoryOverrideForTests = logs.Root;
        SandboxDiagnosticLog.Initialize();
        var payload = new SandboxSetupPayload
        {
            OfflineUsername         = "TinyHarnessOffline",
            OnlineUsername          = "TinyHarnessOnline",
            SandboxHome             = home.Root,
            CommandWorkingDirectory = workspace.Root,
            ReadRoots               = [workspace.Root],
            WriteRoots              = [workspace.Root],
            RealUser                = "tester"
        };
        await WorkspaceAclInheritanceDiagnostics.LogCapabilityContextAsync(payload, workspace.Root,
                                                                           Path.Combine(workspace.Root, ".github"),
                                                                           CancellationToken.None);
        await SandboxDiagnosticLog.FlushForTestsAsync();
        var file = Directory.GetFiles(logs.Root, "sandbox-diagnostic-*.log").Single();
        var text = await File.ReadAllTextAsync(file);
        Assert.That(text, Does.Contain("S-1-5-21-77-77-77-713"));
        Assert.That(text, Does.Contain(".github"));
        Assert.That(text, Does.Contain(home.Root));
    }

    [Test]
    public void LogCapabilityContextAsync_PropagatesProductCancellationInsteadOfSwallowing()
    {
        using var home      = new TestTempDir();
        using var workspace = new TestTempDir();
        File.WriteAllText(Path.Combine(home.Root, "cap_sid"), "{}");
        var payload = new SandboxSetupPayload
        {
            OfflineUsername         = "TinyHarnessOffline",
            OnlineUsername          = "TinyHarnessOnline",
            SandboxHome             = home.Root,
            CommandWorkingDirectory = workspace.Root,
            ReadRoots               = [workspace.Root],
            WriteRoots              = [workspace.Root],
            RealUser                = "tester"
        };
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.CatchAsync<OperationCanceledException>(() =>
                                                          WorkspaceAclInheritanceDiagnostics
                                                             .LogCapabilityContextAsync(payload, workspace.Root,
                                                                       Path.Combine(workspace.Root, ".github"),
                                                                       cancelled.Token));
    }

    [Test]
    public void DescribeAclProbe_ReportsOpenResultAndWin32Error()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TestTempDir();
        var       probe     = WorkspaceAclInheritanceDiagnostics.DescribeAclProbe(workspace.Root);
        Assert.That(probe, Does.Contain("opened=True"));
        Assert.That(probe, Does.Contain("win32Error=0"));
        Assert.That(probe, Does.Contain("identity="), "A successful open reports the volume/index identity.");
        Assert.That(probe, Does.Not.Contain("identity=<unavailable>"));

        var missing = WorkspaceAclInheritanceDiagnostics.DescribeAclProbe(Path.Combine(workspace.Root, "missing"));
        Assert.That(missing, Does.Contain("opened=False"));
        Assert.That(missing, Does.Not.Contain("win32Error=0"), "A failed open must surface its Win32 error code.");
    }

    [Test]
    public void DescribeAclProbe_OpensHeldFileReadonlyWithoutChangingItsDacl()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TestTempDir();
        var       file      = Path.Combine(workspace.Root, "held.txt");
        File.WriteAllText(file, "held");
        var before = new FileInfo(file).GetAccessControl(AccessControlSections.Access)
                                       .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // READ_CONTROL|WRITE_DAC 打开不受共享模式限制，这正是 helper 能在文件被占用时管理 ACL 的原因。
            var probe = WorkspaceAclInheritanceDiagnostics.DescribeAclProbe(file);
            Assert.That(probe, Does.Contain("opened=True"));
            Assert.That(probe, Does.Contain("win32Error=0"));
        }

        var after = new FileInfo(file).GetAccessControl(AccessControlSections.Access)
                                      .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        Assert.That(after, Is.EqualTo(before), "The read-only probe must not change the DACL.");
    }

    [Test]
    public void DescribeAclProbe_ReportsWin32ErrorForInvalidParent()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TestTempDir();
        var       file      = Path.Combine(workspace.Root, "plain.txt");
        File.WriteAllText(file, "plain");
        var probe = WorkspaceAclInheritanceDiagnostics.DescribeAclProbe(Path.Combine(file, "nested"));
        Assert.That(probe, Does.Contain("opened=False"));
        Assert.That(probe, Does.Not.Contain("win32Error=0"), "The raw Win32 error code must be reported.");
        Assert.That(probe, Does.Contain("identity=<unavailable>"));
    }

    [Test]
    public void DescribeAclProbe_DoesNotChangeDirectoryDacl()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TestTempDir();
        var before = new DirectoryInfo(workspace.Root).GetAccessControl(AccessControlSections.Access)
                                                      .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        WorkspaceAclInheritanceDiagnostics.DescribeAclProbe(workspace.Root);
        var after = new DirectoryInfo(workspace.Root).GetAccessControl(AccessControlSections.Access)
                                                     .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        Assert.That(after, Is.EqualTo(before), "Opening READ_CONTROL|WRITE_DAC read-only must not write the DACL.");
    }

    [Test]
    public void DescribeProcessToken_ReportsCurrentProcessWhitelistedFields()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (opened, win32Error, description) =
            SandboxProcessDiagnostics.DescribeProcessToken((uint)Environment.ProcessId);
        Assert.That(opened, Is.True);
        Assert.That(win32Error, Is.EqualTo(0));
        Assert.That(description, Does.Contain("userSid=S-1-"));
        Assert.That(description, Does.Contain("tokenType=primary"));
        Assert.That(description, Does.Contain("elevation="));
        Assert.That(description, Does.Contain("integrity="));
        Assert.That(description, Does.Not.Contain(Environment.UserName),
                    "Only SIDs and token classes are recorded, not account names from arbitrary domains.");
    }

    [Test]
    public void DescribeProcessToken_FailsSilentlyWithWin32ErrorForUnusablePid()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (opened, win32Error, description) = SandboxProcessDiagnostics.DescribeProcessToken(0xFFFFFFF0);
        Assert.That(opened, Is.False);
        Assert.That(win32Error, Is.Not.EqualTo(0));
        Assert.That(description, Does.StartWith("<"));
    }

    [Test]
    public async Task BeginRun_ThreadsRunIdIntoSubsequentEventsOnly()
    {
        using var logs = new TestTempDir();
        SandboxDiagnosticLog.LogDirectoryOverrideForTests = logs.Root;
        SandboxDiagnosticLog.Initialize();
        SandboxDiagnosticLog.BeginRun("ab12cd34");
        SandboxDiagnosticLog.Event("unit", "inside-run");
        SandboxDiagnosticLog.BeginRun(null);
        SandboxDiagnosticLog.Event("unit", "outside-run");
        await SandboxDiagnosticLog.FlushForTestsAsync();
        var file = Directory.GetFiles(logs.Root, "sandbox-diagnostic-*.log").Single();
        var text = await File.ReadAllTextAsync(file);
        Assert.That(text, Does.Contain("|unit|run=ab12cd34|inside-run"));
        Assert.That(text, Does.Contain("|unit|outside-run"));
    }

    [Test]
    public async Task LogInspectionFailure_EmitsHostSubjectProbeEvent()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TestTempDir();
        using var logs      = new TestTempDir();
        SandboxDiagnosticLog.LogDirectoryOverrideForTests = logs.Root;
        SandboxDiagnosticLog.Initialize();
        var child = Path.Combine(workspace.Root, "child");
        Directory.CreateDirectory(child);
        WorkspaceAclInheritanceDiagnostics.LogInspectionFailure(false, workspace.Root, child, true, [], []);
        await SandboxDiagnosticLog.FlushForTestsAsync();
        var file = Directory.GetFiles(logs.Root, "sandbox-diagnostic-*.log").Single();
        var text = await File.ReadAllTextAsync(file);
        Assert.That(text, Does.Contain("|acl-probe|"));
        Assert.That(text, Does.Contain("probeSubject=host"));
        Assert.That(text, Does.Contain("win32Error="));
        Assert.That(text, Does.Contain("childAttributes=0x"));
    }

    [Test]
    public async Task WriteRootAclSnapshot_ReportsRootAndChangeFlagWithoutApiClaims()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var workspace = new TestTempDir();
        using var logs      = new TestTempDir();
        SandboxDiagnosticLog.LogDirectoryOverrideForTests = logs.Root;
        SandboxDiagnosticLog.Initialize();
        var payload = new SandboxSetupPayload
        {
            OfflineUsername         = "TinyHarnessOffline",
            OnlineUsername          = "TinyHarnessOnline",
            SandboxHome             = workspace.Root,
            CommandWorkingDirectory = workspace.Root,
            ReadRoots               = [workspace.Root],
            WriteRoots              = [workspace.Root],
            RealUser                = "tester"
        };
        var before = WorkspaceAclInheritanceDiagnostics.CaptureWriteRootAcls(payload);
        WorkspaceAclInheritanceDiagnostics.LogWriteRootAclSnapshot("before-helper", before);
        WorkspaceAclInheritanceDiagnostics.LogWriteRootAclComparison("after-helper-exit", before, payload);
        await SandboxDiagnosticLog.FlushForTestsAsync();
        var file = Directory.GetFiles(logs.Root, "sandbox-diagnostic-*.log").Single();
        var text = await File.ReadAllTextAsync(file);
        Assert.That(text, Does.Contain("phase=before-helper"));
        Assert.That(text, Does.Contain("phase=after-helper-exit"));
        Assert.That(text, Does.Contain($"root='{workspace.Root}'"));
        Assert.That(text, Does.Contain("changed=False"));
    }

    [Test]
    public void DescribeFileSha256_MatchesKnownHashAndHandlesMissingFile()
    {
        using var workspace = new TestTempDir();
        var       file      = Path.Combine(workspace.Root, "hash.bin");
        File.WriteAllBytes(file, [1, 2, 3]);
        var expected = Convert.ToHexString(SHA256.HashData([1, 2, 3])).ToLowerInvariant();
        Assert.That(SandboxProcessDiagnostics.DescribeFileSha256(file), Is.EqualTo(expected));
        Assert.That(SandboxProcessDiagnostics.DescribeFileSha256(Path.Combine(workspace.Root, "missing.exe")),
                    Does.StartWith("<unavailable:"));
    }
}
#endif
