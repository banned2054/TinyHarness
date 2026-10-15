using WindowsCredentialStore = TinyHarness.Core.Services.Runtime.WindowsCredentialStore;

namespace TinyHarness.Tests;

public class WindowsCredentialStoreTests
{
    [Test]
    public void SaveReadOverwriteDelete_RoundTripsSecretInWindowsCredentialManager()
    {
        if (!OperatingSystem.IsWindows())
            // 仅 win-x64 是已验证的 NativeAOT RID；其他平台跳过。
            return;

        var store = new WindowsCredentialStore();
        Assert.That(store.IsSupported, Is.True);

        var target = $"TinyHarness.Tests:{Guid.NewGuid():N}";
        try
        {
            Assert.That(store.Read(target), Is.Null);

            store.Save(target, "secret-one");
            Assert.That(store.Read(target), Is.EqualTo("secret-one"));

            store.Save(target, "secret-two");
            Assert.That(store.Read(target), Is.EqualTo("secret-two"));

            Assert.That(store.Delete(target), Is.True);
            Assert.That(store.Read(target), Is.Null);
            Assert.That(store.Delete(target), Is.False);
        }
        finally
        {
            try
            {
                store.Delete(target);
            }
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
            {
                // 尽力清理测试凭据。
            }
        }
    }

    [Test]
    public void ReadMissingEntry_ReturnsNull()
    {
        if (!OperatingSystem.IsWindows()) return;

        var store = new WindowsCredentialStore();
        Assert.That(store.Read($"TinyHarness.Tests:{Guid.NewGuid():N}"), Is.Null);
    }
}
