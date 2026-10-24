using TinyHarness.Cli.Services;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         验证启动默认模型的凭据解析组合入口：环境变量缺失（null）与已配置凭据存储条目缺失
///         （ConfigException）在允许降级时都归一为无密钥结果，严格模式下都拒绝启动。
///     </para>
///     <para>
///         Verifies the startup-default credential-resolution composition entry: a missing environment variable
///         (null) and a configured but missing credential-store entry (ConfigException) both normalize to no-key
///         when degradation is allowed, and both reject startup in strict mode.
///     </para>
/// </summary>
public class StartupModelCredentialsTests
{
    [Test]
    public void MissingCredentialStoreEntry_DegradesToNoKeyWhenAllowed()
    {
        // 已配置的凭据存储条目缺失：ApiKeyReader 直接抛 ConfigException，降级允许时归一为 null。
        var store = new FakeCredentialStore { ReadResult = null };

        var key = StartupModelCredentials.Resolve("WORK_KEY", "TinyHarness/work", "work", store,
                                                  allowMissing : true);

        Assert.That(key, Is.Null, "a missing credential-store entry must degrade to a no-key composition");
    }

    [Test]
    public void MissingCredentialStoreEntry_RejectsStartupWhenStrict()
    {
        var store = new FakeCredentialStore { ReadResult = null };

        var ex = TestAssert.Throws<ConfigException>(() =>
                                                         StartupModelCredentials.Resolve("WORK_KEY",
                                                             "TinyHarness/work", "work", store,
                                                             allowMissing : false));

        Assert.That(ex.Message, Does.Contain("No API key found in the credential store entry 'TinyHarness/work'"));
    }

    [Test]
    public void UnsupportedCredentialStore_DegradesToNoKeyWhenAllowed()
    {
        var store = new FakeCredentialStore { ReadResult = null, Supported = false };

        var key = StartupModelCredentials.Resolve("WORK_KEY", "TinyHarness/work", "work", store,
                                                  allowMissing : true);

        Assert.That(key, Is.Null, "an unsupported credential store must degrade like a missing entry");
    }

    [Test]
    public void MissingEnvironmentVariable_DegradesToNoKeyWhenAllowed()
    {
        // 环境变量来源：缺失返回 null；降级允许时同样归一为 null，不抛出。
        var key = StartupModelCredentials.Resolve("TINYHARNESS_PROBE_UNSET_ENV_VAR", string.Empty, "work",
                                                  new FakeCredentialStore { ReadResult = "unused" },
                                                  allowMissing : true);

        Assert.That(key, Is.Null);
    }

    [Test]
    public void MissingEnvironmentVariable_RejectsStartupWhenStrict()
    {
        var ex = TestAssert.Throws<ConfigException>(() =>
                                                        StartupModelCredentials.Resolve(
                                                            "TINYHARNESS_PROBE_UNSET_ENV_VAR", string.Empty, "work",
                                                            new FakeCredentialStore { ReadResult = "unused" },
                                                            allowMissing : false));

        Assert.That(ex.Message, Does.Contain("No API key found"));
        Assert.That(ex.Message, Does.Contain("TINYHARNESS_PROBE_UNSET_ENV_VAR"));
    }

    [Test]
    public void PresentCredential_ReturnsTheKeyRegardlessOfMode()
    {
        var store = new FakeCredentialStore { ReadResult = "stored-key" };

        Assert.That(StartupModelCredentials.Resolve("WORK_KEY", "TinyHarness/work", "work", store,
                                                    allowMissing : true),
                    Is.EqualTo("stored-key"));
        Assert.That(StartupModelCredentials.Resolve("WORK_KEY", "TinyHarness/work", "work", store,
                                                    allowMissing : false),
                    Is.EqualTo("stored-key"));
    }

    /// <summary>
    ///     <para>可控的凭据存储假件：按配置返回条目或模拟缺失，不接触系统存储。</para>
    ///     <para>Controllable credential-store fake: returns the configured entry or simulates absence without touching the system store.</para>
    /// </summary>
    private sealed class FakeCredentialStore : ICredentialStore
    {
        public bool    Supported  { get; init; } = true;
        public string? ReadResult { get; init; }

        public bool IsSupported => Supported;

        public void Save(string targetName, string secret) { }

        public string? Read(string targetName) => ReadResult;

        public bool Delete(string targetName) => false;
    }
}
