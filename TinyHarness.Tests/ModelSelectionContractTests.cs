using System.Text.Json;
using System.Text.Json.Nodes;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Exceptions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.Context;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         验证选择记录 /2 契约：完整快照往返、未知 schema／投影／映射版本拒绝、错误前驱拒绝、
///         历史能力与当前能力分离，以及旧记录缺失信息保持未知。
///     </para>
///     <para>
///         Verifies the /2 selection-record contract: full-snapshot round trips, rejection of unknown schema,
///         projection, or mapping versions, predecessor-chain rejection, separation of recorded from current
///         capabilities, and legacy records keeping missing information unknown.
///     </para>
/// </summary>
public class ModelSelectionContractTests
{
    private static readonly SessionModelIdentity Identity =
        new("contract-model", "https://contract.example/v1", ChatApiKind.ChatCompletions);

    private static SessionModelSelection V2(long revision = 1, long? previous = 0) => new()
    {
        Revision                = revision,
        PreviousRevision        = previous,
        SelectedUtc             = DateTimeOffset.UtcNow,
        Model                   = Identity,
        ProfileHint             = "work",
        CredentialRoute         = "work|env:WORK_KEY",
        Context                 = new ContextOptions { ContextWindowTokens = 128_000 },
        ReasoningEffort         = ModelReasoningEffort.High,
        ReasoningCapabilities   = [ModelReasoningEffort.Low, ModelReasoningEffort.High],
        ReasoningMappingVersion = ReasoningEffortMappings.CurrentVersion,
        ProjectionVersion       = SessionModelSelection.CurrentProjectionVersion,
        MessageBoundary         = 1
    };

    [Test]
    public void FullV2Snapshot_RoundTripsThroughJson()
    {
        var selection = V2(revision : 3, previous : 2);

        var roundTripped = JsonSerializer.SerializeToNode(selection, SessionLogJsonContext.Default.SessionModelSelection)!
                                         .Deserialize(SessionLogJsonContext.Default.SessionModelSelection)!;

        // 记录含数组字段，不做整体相等；逐字段断言完整快照语义。
        Assert.That(() => roundTripped.Validate(), Throws.Nothing);
        Assert.That(roundTripped.Schema, Is.EqualTo(SessionModelSelection.CurrentSchema));
        Assert.That(roundTripped.PreviousRevision, Is.EqualTo(2));
        Assert.That(roundTripped.SelectedUtc, Is.Not.Null);
        Assert.That(roundTripped.ProfileHint, Is.EqualTo("work"));
        Assert.That(roundTripped.ReasoningCapabilities,
                    Is.EqualTo(new[] { ModelReasoningEffort.Low, ModelReasoningEffort.High }));
        Assert.That(roundTripped.ReasoningMappingVersion, Is.EqualTo(ReasoningEffortMappings.CurrentVersion));
        Assert.That(roundTripped.ProjectionVersion, Is.EqualTo(SessionModelSelection.CurrentProjectionVersion));
    }

    [TestCase("schema")]
    [TestCase("projection")]
    [TestCase("mapping")]
    public void UnknownVersions_AreRefusedInsteadOfReinterpreted(string missing)
    {
        var selection = V2();
        var broken = missing switch
        {
            "schema"     => selection with { Schema = "tinyharness.model-selection/9" },
            "projection" => selection with { ProjectionVersion = "tinyharness.history-projection/9" },
            _            => selection with { ReasoningMappingVersion = "future-effort/9" }
        };

        Assert.That(broken.Validate,
                    Throws.TypeOf<InvalidDataException>().With.Message.Contains(missing == "schema"
                        ? "refuses to continue"
                        : "contract"));
    }

    [Test]
    public void MissingPredecessorOrSelectionTime_RejectedOnV2()
    {
        Assert.That((V2() with { PreviousRevision = null }).Validate, Throws.TypeOf<InvalidDataException>());
        Assert.That((V2() with { SelectedUtc = null }).Validate, Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void EffortOutsideRecordedCapabilitySnapshot_Rejected()
    {
        // 历史档位按记录时的能力快照验证；快照已知且不含该档位时拒绝，不参考当前配置。
        Assert.That((V2() with { ReasoningEffort = ModelReasoningEffort.Medium }).Validate,
                    Throws.TypeOf<InvalidDataException>());

        // 快照未知（null）：无从验证，保持未知而不是伪造能力表。
        Assert.That((V2() with { ReasoningCapabilities = null }).Validate, Throws.Nothing);
    }

    [Test]
    public void LegacyV1Records_KeepMissingInformationUnknownAndStayReadable()
    {
        var legacy = new SessionModelSelection
        {
            Schema          = SessionModelSelection.LegacySchemaV1,
            Revision        = 2,
            Model           = Identity,
            CredentialRoute = "work|env:WORK_KEY",
            MessageBoundary = 1
        };

        Assert.That(() => legacy.Validate(), Throws.Nothing);
        Assert.That(legacy.PreviousRevision, Is.Null);
        Assert.That(legacy.SelectedUtc, Is.Null);
        Assert.That(legacy.ProfileHint, Is.Null);
        Assert.That(legacy.ReasoningCapabilities, Is.Null);
        Assert.That(legacy.ReasoningMappingVersion, Is.Null);
        Assert.That(legacy.ProjectionVersion, Is.Null);
        Assert.That(legacy.IsLegacySchema, Is.True);
    }

    [Test]
    public void RecordingChain_RejectsBrokenPredecessors()
    {
        var session = new ChatSession(new string('e', 32), Path.GetTempPath(), Identity);
        session.RecordModelSelection(V2(revision : 1, previous : 0));

        // /2 前驱必须与链上一致；伪造前驱按损坏拒绝，不静默修补。
        Assert.That(() => session.RecordModelSelection(V2(revision : 2, previous : 0)),
                    Throws.TypeOf<InvalidDataException>());
        Assert.That(() => session.RecordModelSelection(V2(revision : 2, previous : 7)),
                    Throws.TypeOf<InvalidDataException>());
        Assert.That(() => session.RecordModelSelection(V2(revision : 3, previous : 1)),
                    Throws.TypeOf<InvalidDataException>().With.Message.Contains("contiguous"));
        session.RecordModelSelection(V2(revision : 2, previous : 1));
        Assert.That(session.ModelSelections, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Reader_RejectsBrokenPredecessorInRealLog()
    {
        using var store          = new TestTempDir();
        var       contextOptions = new ContextOptions { ContextWindowTokens = 128_000 };
        var       session        = new ChatSession(new string('f', 32), Path.GetTempPath(), Identity);
        session.RecordModelSelection(V2(revision : 1, previous : 0) with
        {
            MessageBoundary = 0,
            Context         = contextOptions
        });
        var context = new ConversationContext(contextOptions);
        context.Append(Core.Models.ChatCompletions.ChatMessage.System("sys"));
        // 前驱伪造为 0：写入端单记录校验不拦截，读取端按整链拒绝。
        var forged = V2(revision : 2, previous : 0) with { MessageBoundary = 1, Context = contextOptions };
        var writer = new SessionStoreWriter(store.Root, session.Id);
        try
        {
            await writer.CommitControlAsync(session, context,
                                            [
                                                new SessionLogRecord(string.Empty, "model-selection",
                                                    JsonSerializer.SerializeToElement(forged,
                                                        SessionLogJsonContext.Default.SessionModelSelection))
                                            ],
                                            CancellationToken.None);

            var ex = await TestAssert.ThrowsAsync<SessionStoreException>(
                () => SessionStoreReader.ReadAsync(store.Root, session.Id, CancellationToken.None));
            Assert.That(ex.Message, Does.Contain("Invalid model selection predecessor revision"));
        }
        finally
        {
            writer.Dispose();
        }
    }

    [Test]
    public void RecordedCapabilities_AreIndependentOfCurrentConfiguration()
    {
        // 记录时声明 low/high 并选择 high：历史校验不依赖后来变化的 profile（这里仅靠记录自身成立）。
        var recorded = V2();
        Assert.That(() => recorded.Validate(), Throws.Nothing);

        // 同一记录若档位超出快照则拒绝——即使当前目录仍支持该档位也与历史校验无关。
        Assert.That((recorded with { ReasoningEffort = ModelReasoningEffort.None }).Validate,
                    Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void LegacyRunsWithoutSelectionReference_KeepEffortUnknown()
    {
        // 旧 run 不引用选择修订：强度未知；显式选择 null 档位的 run 则明确表示“服务默认”。
        var legacyRun = new PersistedRun { Id = "run-legacy", StartedUtc = DateTimeOffset.UtcNow };
        Assert.That(legacyRun.ModelSelectionRevision, Is.Null);

        var explicitDefaultRun = new PersistedRun
        {
            Id                     = "run-explicit",
            ModelSelectionRevision = 1,
            StartedUtc             = DateTimeOffset.UtcNow
        };
        Assert.That(explicitDefaultRun.ModelSelectionRevision, Is.EqualTo(1));
    }
}
