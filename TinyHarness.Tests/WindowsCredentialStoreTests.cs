using WindowsCredentialStore = TinyHarness.Core.Services.Runtime.WindowsCredentialStore;

namespace TinyHarness.Tests;

public class WindowsCredentialStoreTests
{
    [Fact]
    public void SaveReadOverwriteDelete_RoundTripsSecretInWindowsCredentialManager()
    {
        if (!OperatingSystem.IsWindows())
        {
            // Only win-x64 is a verified NativeAOT RID; skip elsewhere.
            return;
        }

        var store = new WindowsCredentialStore();
        Assert.True(store.IsSupported);

        var target = $"TinyHarness.Tests:{Guid.NewGuid():N}";
        try
        {
            Assert.Null(store.Read(target));

            store.Save(target, "secret-one");
            Assert.Equal("secret-one", store.Read(target));

            store.Save(target, "secret-two");
            Assert.Equal("secret-two", store.Read(target));

            Assert.True(store.Delete(target));
            Assert.Null(store.Read(target));
            Assert.False(store.Delete(target));
        }
        finally
        {
            try
            {
                store.Delete(target);
            }
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
            {
                // Best-effort cleanup of the test credential.
            }
        }
    }

    [Fact]
    public void ReadMissingEntry_ReturnsNull()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new WindowsCredentialStore();
        Assert.Null(store.Read($"TinyHarness.Tests:{Guid.NewGuid():N}"));
    }
}
