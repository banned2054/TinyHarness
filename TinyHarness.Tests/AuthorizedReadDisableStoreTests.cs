using TinyHarness.Core.Models.Runtime.WindowsSandbox;
using TinyHarness.Core.Services.Runtime.WindowsSandbox;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         授权执行禁用状态文件契约：文件缺失为未禁用；存在但缺少 disabled、disabled 为 null 或 JSON
///         损坏时一律保持禁用；读取本身绝不解除禁用，只有成功恢复后的显式清除才删除状态。
///     </para>
///     <para>
///         The authorized-execution disable-state file contract: a missing file means not disabled; an
///         existing file with a missing or null disabled property, or corrupt JSON, stays blocking; reading
///         never clears the state, and only an explicit clear after a successful restore deletes it.
///     </para>
/// </summary>
public class AuthorizedReadDisableStoreTests
{
    [Test]
    public async Task Read_MissingFile_IsNotDisabled()
    {
        using var fixture = new Fixture();

        var (disabled, reason) = await fixture.Store.ReadAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(disabled, Is.False);
            Assert.That(reason, Is.Null);
        });
    }

    [Test]
    public async Task Read_DisabledState_ReturnsThePersistedReason()
    {
        using var fixture = new Fixture();
        await fixture.WriteAsync("""{"disabled":true,"reason":"restore failed"}""");

        var (disabled, reason) = await fixture.Store.ReadAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(disabled, Is.True);
            Assert.That(reason, Is.EqualTo("restore failed"));
        });
    }

    [Test]
    public async Task Read_MissingDisabledProperty_StaysDisabled()
    {
        using var fixture = new Fixture();
        await fixture.WriteAsync("""{"reason":"restore failed"}""");

        var (disabled, reason) = await fixture.Store.ReadAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(disabled, Is.True, "Incomplete persisted state must fail closed.");
            Assert.That(reason, Does.Contain("unreadable"));
            Assert.That(File.Exists(fixture.StatePath), Is.True, "Reading must not clear the state file.");
        });
    }

    [Test]
    public async Task Read_NullDisabledProperty_StaysDisabled()
    {
        using var fixture = new Fixture();
        await fixture.WriteAsync("""{"disabled":null,"reason":"restore failed"}""");

        var (disabled, reason) = await fixture.Store.ReadAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(disabled, Is.True);
            Assert.That(reason, Does.Contain("unreadable"));
            Assert.That(File.Exists(fixture.StatePath), Is.True);
        });
    }

    [Test]
    public async Task Read_ExplicitlyNotDisabledState_StaysDisabled()
    {
        using var fixture = new Fixture();
        await fixture.WriteAsync("""{"disabled":false,"reason":"restore failed"}""");

        var (disabled, reason) = await fixture.Store.ReadAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(disabled, Is.True, "An existing state file must never un-disable by content alone.");
            Assert.That(reason, Does.Contain("unreadable"));
        });
    }

    [Test]
    public async Task Read_JsonNull_StaysDisabled()
    {
        using var fixture = new Fixture();
        await fixture.WriteAsync("null");

        var (disabled, reason) = await fixture.Store.ReadAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(disabled, Is.True);
            Assert.That(reason, Does.Contain("unreadable"));
        });
    }

    [Test]
    public async Task Read_MalformedJson_StaysDisabled()
    {
        using var fixture = new Fixture();
        await fixture.WriteAsync("""{"disabled":true,"reason":""");

        var (disabled, reason) = await fixture.Store.ReadAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(disabled, Is.True);
            Assert.That(reason, Does.Contain("unreadable"));
        });
    }

    [Test]
    public async Task Read_IncompleteStateTwice_StaysDisabledUntilExplicitClear()
    {
        using var fixture = new Fixture();
        await fixture.WriteAsync("""{"reason":"restore failed"}""");

        var first  = await fixture.Store.ReadAsync(CancellationToken.None);
        var second = await fixture.Store.ReadAsync(CancellationToken.None);
        await fixture.Store.ClearAsync(CancellationToken.None);
        var cleared = await fixture.Store.ReadAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(first.Disabled, Is.True);
            Assert.That(second.Disabled, Is.True, "Re-reading must not silently un-disable the state.");
            Assert.That(cleared.Disabled, Is.False, "Only an explicit clear after recovery removes the block.");
        });
    }

    [Test]
    public async Task DisableThenClear_RoundTripsThroughTheFile()
    {
        using var fixture = new Fixture();

        await fixture.Store.DisableAsync("restore failed", CancellationToken.None);
        var disabled = await fixture.Store.ReadAsync(CancellationToken.None);
        await fixture.Store.ClearAsync(CancellationToken.None);
        var cleared = await fixture.Store.ReadAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(disabled.Disabled, Is.True);
            Assert.That(disabled.Reason, Is.EqualTo("restore failed"));
            Assert.That(cleared.Disabled, Is.False);
            Assert.That(File.Exists(fixture.StatePath), Is.False);
        });
    }

    [Test]
    public async Task ReadState_DistinguishesRestoreFailedReclamationUncertainAndLegacyUnknown()
    {
        using var fixture = new Fixture();

        await fixture.Store.DisableAsync("restore failed", AuthorizedReadDisableKind.RestoreFailed, null,
                                         CancellationToken.None);
        var restoreState = await fixture.Store.ReadStateAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(restoreState.Disabled, Is.True);
            Assert.That(restoreState.Kind, Is.EqualTo(AuthorizedReadDisableKind.RestoreFailed));
            Assert.That(restoreState.BootId, Is.Null);
        });

        await fixture.Store.DisableAsync("whole tree unconfirmed", AuthorizedReadDisableKind.ReclamationUncertain, 42,
                                         CancellationToken.None);
        var uncertainState = await fixture.Store.ReadStateAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(uncertainState.Disabled, Is.True);
            Assert.That(uncertainState.Kind, Is.EqualTo(AuthorizedReadDisableKind.ReclamationUncertain));
            Assert.That(uncertainState.BootId, Is.EqualTo(42));
        });

        // 缺少 kind 的旧状态文件按保守 Unknown 分类，防止绕过退出未知限制。
        await fixture.WriteAsync("""{"disabled":true,"reason":"legacy disable"}""");
        var legacyState = await fixture.Store.ReadStateAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(legacyState.Disabled, Is.True);
            Assert.That(legacyState.Kind, Is.EqualTo(AuthorizedReadDisableKind.Unknown));
        });
    }

    [Test]
    public async Task SharedStateDirectory_IsSharedAcrossHomesAndDualReadsLegacyState()
    {
        using var root      = new TestTempDir();
        var       sharedDir = Path.Combine(root.Root, "shared-auth-state");
        var       stdHome   = Path.Combine(root.Root, "standard-home");
        var       tcpHome   = Path.Combine(root.Root, "tcp-loopback-home");
        var stdComponents = new WindowsSandboxComponents
        {
            SetupExecutablePath  = Path.Combine(root.Root, "setup.exe"),
            RunnerExecutablePath = Path.Combine(root.Root, "runner.exe"),
            SandboxHome          = stdHome
        };
        var tcpComponents = new WindowsSandboxComponents
        {
            SetupExecutablePath  = Path.Combine(root.Root, "setup.exe"),
            RunnerExecutablePath = Path.Combine(root.Root, "runner.exe"),
            SandboxHome          = tcpHome,
            AllowTcpLoopback     = true
        };
        var stdStore = new AuthorizedReadDisableFile(stdComponents, sharedDir);
        var tcpStore = new AuthorizedReadDisableFile(tcpComponents, sharedDir);

        // 标准模式写入禁用后，共享同一授权组的 TCP 环回模式立即读取到相同禁用状态。
        await stdStore.DisableAsync("uncertain on standard home", AuthorizedReadDisableKind.ReclamationUncertain, 77,
                                    CancellationToken.None);
        var seenByTcp = await tcpStore.ReadStateAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(seenByTcp.Disabled, Is.True);
            Assert.That(seenByTcp.Kind, Is.EqualTo(AuthorizedReadDisableKind.ReclamationUncertain));
            Assert.That(seenByTcp.BootId, Is.EqualTo(77));
            TestAssert.Contains("standard home", seenByTcp.Reason!, StringComparison.Ordinal);
        });

        // 兼容双读旧版每 home 凭据目录状态，并按最严格类别合并。
        await tcpStore.ClearAsync(CancellationToken.None);
        Directory.CreateDirectory(stdComponents.CredentialsDirectory);
        await File.WriteAllTextAsync(stdStore.LegacyHomeStatePath,
                                     """{"disabled":true,"reason":"legacy uncertain","kind":"reclamation_uncertain","boot_id":88}""");
        var legacySeen = await stdStore.ReadStateAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(legacySeen.Disabled, Is.True);
            Assert.That(legacySeen.Kind, Is.EqualTo(AuthorizedReadDisableKind.ReclamationUncertain));
            Assert.That(legacySeen.BootId, Is.EqualTo(88));
        });

        await stdStore.ClearAsync(CancellationToken.None);
        var afterClear = await stdStore.ReadStateAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(afterClear.Disabled, Is.False);
            Assert.That(File.Exists(stdStore.LegacyHomeStatePath), Is.False);
        });
    }

    [Test]
    public async Task ReadState_UnreadableFileFailsClosedInsteadOfTreatingAsMissing()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        await fixture.WriteAsync("""{"disabled":true,"reason":"locked state","kind":"restore_failed"}""");

        // 独占锁定文件模拟不可读：绝不能像 File.Exists 那样把访问失败静默当成“文件不存在”。
        using var exclusive = new FileStream(fixture.StatePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var state = await fixture.Store.ReadStateAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(state.Disabled, Is.True);
            Assert.That(state.Kind, Is.EqualTo(AuthorizedReadDisableKind.Unknown));
            TestAssert.Contains("unreadable", state.Reason!, StringComparison.Ordinal);
        });
    }

    [Test]
    public async Task ReadState_SharesDeleteAccessSoConcurrentClearAndReplaceSucceed()
    {
        using var fixture = new Fixture();
        await fixture.Store.DisableAsync("window open", AuthorizedReadDisableKind.WindowOpen, 55,
                                         CancellationToken.None);

        // 模拟锁外读以 FileShare.ReadWrite | FileShare.Delete 打开状态文件时，窗口内的原子覆盖替换
        // （WindowOpen -> RestoreFailed）仍可成功，且旧句柄继续读到旧快照，新读端立即看到新状态。
        string oldContent;
        using (var openReader = new FileStream(fixture.StatePath, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete))
        {
            var readBeforeReplace = await fixture.Store.ReadStateAsync(CancellationToken.None);
            Assert.That(readBeforeReplace.Disabled, Is.True);
            Assert.That(readBeforeReplace.Kind, Is.EqualTo(AuthorizedReadDisableKind.WindowOpen));
            Assert.That(readBeforeReplace.SourceSandboxHome, Is.Not.Null.And.Not.Empty);

            await fixture.Store.DisableAsync("restore failed while reader open",
                                             AuthorizedReadDisableKind.RestoreFailed, null,
                                             CancellationToken.None);

            using var streamReader = new StreamReader(openReader, leaveOpen: true);
            oldContent = await streamReader.ReadToEndAsync(CancellationToken.None);
        }

        var afterReplace = await fixture.Store.ReadStateAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            TestAssert.Contains("window_open", oldContent, StringComparison.Ordinal);
            Assert.That(afterReplace.Disabled, Is.True);
            Assert.That(afterReplace.Kind, Is.EqualTo(AuthorizedReadDisableKind.RestoreFailed));
            TestAssert.Contains("restore failed while reader open", afterReplace.Reason!, StringComparison.Ordinal);
        });

        // 同样验证持 FileShare.ReadWrite | FileShare.Delete 读句柄期间，ClearAsync 仍可顺利清除。
        using (new FileStream(fixture.StatePath, FileMode.Open, FileAccess.Read,
                              FileShare.ReadWrite | FileShare.Delete))
        {
            await fixture.Store.ClearAsync(CancellationToken.None);
        }

        var afterClear = await fixture.Store.ReadStateAsync(CancellationToken.None);
        Assert.That(afterClear.Disabled, Is.False);
    }

    [Test]
    public async Task DisableAsync_TransientSharingConflictWithoutDeleteShare_RetriesAndReplacesState()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var fixture = new Fixture();
        await fixture.Store.DisableAsync("window open before conflict", AuthorizedReadDisableKind.WindowOpen, 56,
                                         CancellationToken.None);

        // 模拟外部进程短暂以不含 FileShare.Delete 的方式占用状态文件，有界重试在释放后必须顺利完成替换。
        var blocker = new FileStream(fixture.StatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var releaseTask = Task.Run(async () =>
        {
            await Task.Delay(40);
            blocker.Dispose();
        });

        try
        {
            await fixture.Store.DisableAsync("restore failed after transient conflict",
                                             AuthorizedReadDisableKind.RestoreFailed, null,
                                             CancellationToken.None);
        }
        finally
        {
            await releaseTask;
            blocker.Dispose();
        }

        var replaced = await fixture.Store.ReadStateAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(replaced.Disabled, Is.True);
            Assert.That(replaced.Kind, Is.EqualTo(AuthorizedReadDisableKind.RestoreFailed));
            TestAssert.Contains("restore failed after transient conflict", replaced.Reason!, StringComparison.Ordinal);
        });
    }

    [Test]
    public void MergeStates_RetainsEveryEntryConstraintWithoutDroppingWindowOpenBootId()
    {
        var primary = AuthorizedReadDisableState.DisabledState(
            "old boot uncertain", AuthorizedReadDisableKind.ReclamationUncertain, 100, @"C:\home-a");
        var legacy = AuthorizedReadDisableState.DisabledState(
            "current boot window open", AuthorizedReadDisableKind.WindowOpen, 101, @"C:\home-a");

        var merged = AuthorizedReadDisableFile.MergeStates(primary, legacy);
        Assert.Multiple(() =>
        {
            Assert.That(merged.Disabled, Is.True);
            Assert.That(merged.ActiveEntries, Has.Count.EqualTo(2));
            Assert.That(merged.ActiveEntries.Any(e => e.Kind == AuthorizedReadDisableKind.ReclamationUncertain &&
                                                      e.BootId == 100), Is.True);
            Assert.That(merged.ActiveEntries.Any(e => e.Kind == AuthorizedReadDisableKind.WindowOpen &&
                                                      e.BootId == 101), Is.True);
        });
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestTempDir _dir = new();

        public Fixture()
        {
            var components = new WindowsSandboxComponents
            {
                SetupExecutablePath  = Path.Combine(_dir.Root, "setup.exe"),
                RunnerExecutablePath = Path.Combine(_dir.Root, "runner.exe"),
                SandboxHome          = _dir.Root
            };
            Directory.CreateDirectory(components.CredentialsDirectory);
            Store = new AuthorizedReadDisableFile(components, components.CredentialsDirectory);
        }

        public AuthorizedReadDisableFile Store { get; }

        public string StatePath => Store.StatePath;

        public Task WriteAsync(string json) => File.WriteAllTextAsync(StatePath, json);

        public void Dispose() => _dir.Dispose();
    }
}
