using System.Text.Json.Nodes;
using TinyHarness.Cli;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Permissions;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         验证“使用当前模型”的 AI 审核目标必须同时匹配模型、endpoint 与协议；任一身份漂移即失败关闭，
///         不得把审批上下文发送到配置变化后的新目标。显式 Reviewer 路径保持独立可用。
///     </para>
///     <para>
///         Verifies that "use current model" reviewer resolution requires the model, endpoint and protocol to
///         match the bound session; any identity drift fails closed instead of sending approval context to the
///         changed target. The explicit reviewer path remains independently available.
///     </para>
/// </summary>
public class ReviewerTargetResolutionTests
{
    private static readonly SessionModelIdentity Bound =
        new("model-a", "https://old.example/v1", ChatApiKind.ChatCompletions);

    [Test]
    public void ProfileHint_ModelIdOnlyMatchWithDifferentEndpoint_IsRejected()
    {
        var settings = new PermissionSettings { UseCurrentModel = true };
        var profiles = Catalog("https://new.example/v1").Profiles;

        var target = Program.ResolveReviewerTarget(profiles, settings, Bound, Selection("p"));

        Assert.That(target, Is.Null, "An endpoint change must invalidate the reviewer target even when the model id matches.");
    }

    [Test]
    public void ProfileHint_ModelIdOnlyMatchWithDifferentProtocol_IsRejected()
    {
        var settings = new PermissionSettings { UseCurrentModel = true };
        var profiles = Catalog("https://old.example/v1", ChatApiKind.Responses).Profiles;

        var target = Program.ResolveReviewerTarget(profiles, settings, Bound, Selection("p"));

        Assert.That(target, Is.Null, "A protocol change must invalidate the reviewer target even when the model id matches.");
    }

    [Test]
    public void ProfileHint_CompleteIdentityMatch_ResolvesTheProfileAndModel()
    {
        var settings = new PermissionSettings { UseCurrentModel = true };
        var profiles = Catalog("https://old.example/v1").Profiles;

        var target = Program.ResolveReviewerTarget(profiles, settings, Bound, Selection("p"));

        Assert.That(target, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(target!.Value.Profile.Name, Is.EqualTo("p"));
            Assert.That(target.Value.Model.Id, Is.EqualTo("model-a"));
        });
    }

    [Test]
    public void CredentialRouteHint_ModelIdOnlyMatchWithDifferentEndpoint_IsRejected()
    {
        var settings = new PermissionSettings { UseCurrentModel = true };
        var profiles = Catalog("https://new.example/v1").Profiles;

        var target = Program.ResolveReviewerTarget(profiles, settings, Bound, Selection(null, "p|env:OLD_KEY"));

        Assert.That(target, Is.Null);
    }

    [Test]
    public void WithoutProfileHint_CompleteIdentityMatch_ResolvesTheOnlyMatch()
    {
        var settings = new PermissionSettings { UseCurrentModel = true };
        var profiles = Catalog("https://old.example/v1").Profiles;

        var target = Program.ResolveReviewerTarget(profiles, settings, Bound, null);

        Assert.That(target, Is.Not.Null);
        Assert.That(target!.Value.Profile.Name, Is.EqualTo("p"));
    }

    [Test]
    public void ExplicitReviewer_IgnoresTheBoundSessionIdentity()
    {
        var settings = new PermissionSettings
        {
            UseCurrentModel = false, ReviewerProfile = "reviewer", ReviewerModel = "review-model"
        };
        var profiles = new List<UserProfile>
        {
            Profile("reviewer", "https://reviewer.example/v1", ChatApiKind.Responses, "review-model")
        };

        var target = Program.ResolveReviewerTarget(profiles, settings, Bound, Selection("p"));

        Assert.That(target, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(target!.Value.Profile.Name, Is.EqualTo("reviewer"));
            Assert.That(target.Value.Model.Id, Is.EqualTo("review-model"));
        });
    }

    [Test]
    public async Task MismatchedProfileHint_FailsClosedWithoutReadingCredentialsOrReachingTheHuman()
    {
        using var dir = new TestTempDir();
        var       configPath = Path.Combine(dir.Root, "user-config.json");
        await UserConfigStore.SaveAsync(configPath, Catalog("https://new.example/v1"), CancellationToken.None);
        var session = new ChatSession(Guid.NewGuid().ToString("N"), dir.Root, Bound);
        session.RecordModelSelection(Selection("p"));
        using var host = new ChatSessionHost(session, new ConversationContext(),
                                             new AgentOptions
                                             {
                                                 Model                     = Bound.ModelId,
                                                 MaxAgentSteps             = 2,
                                                 DefaultToolTimeoutSeconds = 10
                                             }, new FakeChatClient(), new ToolRegistry([]), "sys");
        var credentials = new CountingCredentialStore();
        var secrets     = new Dictionary<string, string>();
        var reviewer = new AiReviewer(token => Program
                                              .CreateReviewerAsync(host, secrets, token, configPath, credentials));
        var human    = new CountingApprover();
        var provider = new AiRoutedApprovalProvider(human, reviewer,
                                                    () => SessionApprovalMode.AIAutoReview,
                                                    Context);

        var resolution = await provider.PromptAsync(Request(), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(resolution.Kind, Is.EqualTo(ApprovalResolutionKind.Unavailable),
                        "A mismatched reviewer target must fail closed.");
            Assert.That(human.Calls, Is.Zero, "The human channel is not a fallback for reviewer resolution failures.");
            Assert.That(credentials.Reads, Is.Zero, "Credentials must not be read for a mismatched target.");
            Assert.That(secrets, Is.Empty, "No reviewer credential may be registered.");
        });
    }

    private static UserConfig Catalog(string endpoint, ChatApiKind chatApi = ChatApiKind.ChatCompletions) => new()
    {
        DefaultProfile = "p",
        Profiles       = [Profile("p", endpoint, chatApi, "model-a")],
        Settings = new UserConfigSettings
        {
            Permissions = new PermissionSettings { UseCurrentModel = true }
        }
    };

    private static UserProfile Profile(string name, string endpoint, ChatApiKind chatApi, string modelId) => new()
    {
        Name                   = name,
        Endpoint               = endpoint,
        ChatApi                = chatApi,
        ApiKeyCredentialTarget = "fake-target",
        DefaultModel           = modelId,
        Models                 = [new UserProfileModel { Id = modelId, ContextWindowTokens = 128_000 }]
    };

    private static SessionModelSelection Selection(string? profileHint, string? credentialRoute = null) => new()
    {
        Schema          = SessionModelSelection.LegacySchemaV1,
        Revision        = 1,
        Model           = Bound,
        ProfileHint     = profileHint,
        CredentialRoute = credentialRoute
    };

    private static ApprovalRequest Request() => new(new ToolPreparation
    {
        ToolName   = "shell",
        CallId     = "call_1",
        Arguments  = new JsonObject { ["command"] = "dotnet test" },
        Capability = "process.execute",
        Summary    = "shell: dotnet test"
    }, PermissionLevel.Level1, [GrantScope.SingleOperation], null, null);

    private static ApprovalContext Context() => new(
    [
        new ApprovalContextItem(ApprovalInputSource.UserUtterance, "turn-1", "Run the tests.")
    ]);

    private sealed class CountingCredentialStore : ICredentialStore
    {
        public int Reads { get; private set; }

        public bool IsSupported => true;

        public string? Read(string targetName)
        {
            Reads++;
            return "fake-key";
        }

        public void Save(string targetName, string secret) =>
            throw new AssertionException("A mismatched reviewer target must not save credentials.");

        public bool Delete(string targetName) =>
            throw new AssertionException("A mismatched reviewer target must not delete credentials.");
    }

    private sealed class CountingApprover : IApprovalProvider
    {
        public int Calls { get; private set; }

        public Task<ApprovalResolution> PromptAsync(ApprovalRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(ApprovalResolution.Denied("human fallback must not be reached"));
        }
    }
}
