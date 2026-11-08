using System.Runtime.CompilerServices;
using NUnit.Framework;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Desktop.Runtime;

namespace TinyHarness.Desktop.Tests;

/// <summary>
///     <para>
///         Desktop 组合接线：工厂产出的宿主必须携带真实指纹守卫——配置未变时发送不受影响；
///         真实配置文件变化后发送被阻断并给出会话失效提示，后续发送快速失败。
///     </para>
///     <para>
///         Desktop composition wiring: the factory-produced host must carry a real fingerprint guard —
///         sends flow while the config is unchanged; after a real config edit the send is blocked with
///         the session-invalidated notice and later sends fail fast.
///     </para>
/// </summary>
[TestFixture]
public sealed class DesktopCompositionGuardWiringTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    [Test]
    public async Task FactoryWiresRealGuard_ConfigEditBlocksSendAndLatches()
    {
        var fixture = await Fixture.CreateAsync();
        var factory = new DesktopSessionFactory((_, _) => new ScriptModel(), new Credentials(), fixture.Config);
        await using var session = await factory.OpenAsync(new DesktopSessionRequest(fixture.Root),
                                                          _ => ValueTask.CompletedTask,
                                                          _ => ValueTask.CompletedTask, default);

        // 配置未变：守卫取值与基线一致，发送正常运行。
        var first = await session.SubmitAsync("第一次发送").WaitAsync(Limit);
        Assert.That(first.Result!.Error, Is.Null);

        // 编辑真实用户配置（受保护会话存储目录段落）：守卫取值器重读后与组合基线失配。
        var config = await UserConfigStore.LoadAsync(fixture.Config, default);
        var edited = config with
        {
            Settings = config.Settings! with
            {
                SessionStoreDirectory = Path.Combine(Path.GetDirectoryName(fixture.Store)!, "sessions-moved")
            }
        };
        await UserConfigStore.SaveAsync(fixture.Config, edited, config, default);

        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                          session.SubmitAsync("第二次发送").WaitAsync(Limit));
        Assert.That(blocked!.Message, Does.Contain("常设授权配置已变更"));

        // 失效闩：后续发送立即快速失败。
        var again = await Assert.ThrowsAsync<InvalidOperationException>(() => session.SubmitAsync("第三次发送"));
        Assert.That(again!.Message, Does.Contain("常设授权配置已变更"));
    }

    private sealed record Fixture(string Root, string Store, string Config)
    {
        public static async Task<Fixture> CreateAsync()
        {
            var parent = Path.Combine(Path.GetTempPath(),
                                      "tinyharness-composition-guard-" + Guid.NewGuid().ToString("N"));
            var fixture = new Fixture(Path.Combine(parent, "workspace"), Path.Combine(parent, "sessions"),
                                      Path.Combine(parent, "user.json"));
            Directory.CreateDirectory(fixture.Root);
            await UserConfigStore.SaveAsync(fixture.Config, new UserConfig
            {
                DefaultProfile = "offline",
                Profiles =
                [
                    new UserProfile
                    {
                        Name = "offline", Endpoint = "https://offline.invalid/v1", ApiKeyCredentialTarget = "fixture",
                        DefaultModel = "offline",
                        Models = [new UserProfileModel { Id = "offline", ContextWindowTokens = 128_000 }]
                    }
                ],
                Settings = new UserConfigSettings { SessionStoreDirectory = fixture.Store }
            }, default);
            return fixture;
        }
    }

    private sealed class Credentials : ICredentialStore
    {
        public bool    IsSupported                               => true;
        public string? Read(string   targetName)                 => "offline-key";
        public void    Save(string   targetName, string secret)  => throw new NotSupportedException();
        public bool    Delete(string targetName)                 => throw new NotSupportedException();
    }

    private sealed class ScriptModel : IChatCompletionClient
    {
        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(
            ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "done" };
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "stop" };
        }
    }
}
