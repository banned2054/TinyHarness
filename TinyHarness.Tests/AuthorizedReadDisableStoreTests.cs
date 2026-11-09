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
            Store = new AuthorizedReadDisableFile(components);
        }

        public AuthorizedReadDisableFile Store { get; }

        public string StatePath => Store.StatePath;

        public Task WriteAsync(string json) => File.WriteAllTextAsync(StatePath, json);

        public void Dispose() => _dir.Dispose();
    }
}
