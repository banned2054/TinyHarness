using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.Configuration;
using TinyHarness.Core.Services.Runtime;

namespace TinyHarness.Tests;

public class ApiKeyReaderTests
{
    private const string EnvVariable = "TINYHARNESS_TESTS_API_KEY_READER";

    [Fact]
    public void CredentialTarget_TakesPrecedenceOverEnvironmentVariable()
    {
        var store = new FakeCredentialStore { ["TinyHarness:work"] = "store-secret" };
        Environment.SetEnvironmentVariable(EnvVariable, "env-secret", EnvironmentVariableTarget.Process);
        try
        {
            var status = ApiKeyReader.Describe(EnvVariable, "TinyHarness:work", store);
            var value  = ApiKeyReader.Read(EnvVariable, "TinyHarness:work", store, "work");

            Assert.Equal(ApiKeySourceKind.CredentialStore, status.Kind);
            Assert.True(status.Available);
            Assert.Equal("store-secret", value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVariable, null, EnvironmentVariableTarget.Process);
        }
    }

    [Fact]
    public void EnvironmentVariableUsed_WhenNoCredentialTarget()
    {
        Environment.SetEnvironmentVariable(EnvVariable, "env-secret", EnvironmentVariableTarget.Process);
        try
        {
            var status = ApiKeyReader.Describe(EnvVariable, string.Empty, new FakeCredentialStore());
            var value  = ApiKeyReader.Read(EnvVariable, string.Empty, new FakeCredentialStore());

            Assert.Equal(ApiKeySourceKind.EnvironmentVariable, status.Kind);
            Assert.Equal(EnvVariable, status.SourceName);
            Assert.True(status.Available);
            Assert.Equal("env-secret", value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVariable, null, EnvironmentVariableTarget.Process);
        }
    }

    [Fact]
    public void ConfiguredCredentialThatCannotBeRead_ThrowsWithFixHints()
    {
        var error = Assert.Throws<ConfigException>(() => ApiKeyReader
                                                      .Read(EnvVariable, "TinyHarness:missing",
                                                            new FakeCredentialStore(), "work"));

        Assert.Contains("TinyHarness:missing", error.Message);
        Assert.Contains("auth set work", error.Message);
    }

    [Fact]
    public void UnsupportedPlatform_ThrowsWithEnvironmentVariableHint()
    {
        var store = new FakeCredentialStore { PlatformSupported = false };

        var error = Assert.Throws<ConfigException>(() => ApiKeyReader
                                                      .Read(EnvVariable, "TinyHarness:work", store, "work"));

        Assert.Contains("--env", error.Message);
    }

    [Fact]
    public void NoSourceConfigured_ReturnsNullAndNotAvailable()
    {
        var status = ApiKeyReader.Describe(string.Empty, string.Empty, new FakeCredentialStore());
        var value  = ApiKeyReader.Read(string.Empty, string.Empty, new FakeCredentialStore());

        Assert.Equal(ApiKeySourceKind.None, status.Kind);
        Assert.False(status.Available);
        Assert.Null(value);
    }

    /// <summary>
    /// Fake credential store for offline tests; entries live in memory only.
    /// </summary>
    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _entries = new(StringComparer.Ordinal);

        public bool PlatformSupported { get; init; } = true;

        public bool IsSupported => PlatformSupported;

        public string this[string targetName]
        {
            init => _entries[targetName] = value;
        }

        public void Save(string targetName, string secret) => _entries[targetName] = secret;

        public string? Read(string targetName) => _entries.GetValueOrDefault(targetName);

        public bool Delete(string targetName) => _entries.Remove(targetName);
    }
}
