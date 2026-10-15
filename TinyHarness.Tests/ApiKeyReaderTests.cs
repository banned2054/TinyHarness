using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.Configuration;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Tests;

public class ApiKeyReaderTests
{
    private const string EnvVariable = "TINYHARNESS_TESTS_API_KEY_READER";

    [Test]
    public void CredentialTarget_TakesPrecedenceOverEnvironmentVariable()
    {
        var store = new FakeCredentialStore { ["TinyHarness:work"] = "store-secret" };
        Environment.SetEnvironmentVariable(EnvVariable, "env-secret", EnvironmentVariableTarget.Process);
        try
        {
            var status = ApiKeyReader.Describe(EnvVariable, "TinyHarness:work", store);
            var value  = ApiKeyReader.Read(EnvVariable, "TinyHarness:work", store, "work");

            Assert.That(status.Kind, Is.EqualTo(ApiKeySourceKind.CredentialStore));
            Assert.That(status.Available, Is.True);
            Assert.That(value, Is.EqualTo("store-secret"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVariable, null, EnvironmentVariableTarget.Process);
        }
    }

    [Test]
    public void EnvironmentVariableUsed_WhenNoCredentialTarget()
    {
        Environment.SetEnvironmentVariable(EnvVariable, "env-secret", EnvironmentVariableTarget.Process);
        try
        {
            var status = ApiKeyReader.Describe(EnvVariable, string.Empty, new FakeCredentialStore());
            var value  = ApiKeyReader.Read(EnvVariable, string.Empty, new FakeCredentialStore());

            Assert.That(status.Kind, Is.EqualTo(ApiKeySourceKind.EnvironmentVariable));
            Assert.That(status.SourceName, Is.EqualTo(EnvVariable));
            Assert.That(status.Available, Is.True);
            Assert.That(value, Is.EqualTo("env-secret"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVariable, null, EnvironmentVariableTarget.Process);
        }
    }

    [Test]
    public void ConfiguredCredentialThatCannotBeRead_ThrowsWithFixHints()
    {
        var error = TestAssert.Throws<ConfigException>(() => ApiKeyReader
                                                          .Read(EnvVariable, "TinyHarness:missing",
                                                                new FakeCredentialStore(), "work"));

        Assert.That(error.Message, Does.Contain("TinyHarness:missing"));
        Assert.That(error.Message, Does.Contain("auth set work"));
    }

    [Test]
    public void UnsupportedPlatform_ThrowsWithEnvironmentVariableHint()
    {
        var store = new FakeCredentialStore { PlatformSupported = false };

        var error = TestAssert.Throws<ConfigException>(() => ApiKeyReader
                                                          .Read(EnvVariable, "TinyHarness:work", store, "work"));

        Assert.That(error.Message, Does.Contain("--env"));
    }

    [Test]
    public void NoSourceConfigured_ReturnsNullAndNotAvailable()
    {
        var status = ApiKeyReader.Describe(string.Empty, string.Empty, new FakeCredentialStore());
        var value  = ApiKeyReader.Read(string.Empty, string.Empty, new FakeCredentialStore());

        Assert.That(status.Kind, Is.EqualTo(ApiKeySourceKind.None));
        Assert.That(status.Available, Is.False);
        Assert.That(value, Is.Null);
    }

    /// <summary>
    ///     <para>离线测试用的 fake 凭据存储；条目仅存在于内存。</para>
    ///     <para>Fake credential store for offline tests; entries live in memory only.</para>
    /// </summary>
    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);

        public bool PlatformSupported { get; init; } = true;

        public string this[string targetName]
        {
            init => _entries[targetName] = value;
        }

        public bool IsSupported => PlatformSupported;

        public void Save(string targetName, string secret)
        {
            _entries[targetName] = secret;
        }

        public string? Read(string targetName) => _entries.GetValueOrDefault(targetName);

        public bool Delete(string targetName) => _entries.Remove(targetName);
    }
}
