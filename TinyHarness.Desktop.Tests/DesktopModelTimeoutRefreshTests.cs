using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using NUnit.Framework;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Models.Permissions;
using TinyHarness.Core.Models.Persistence;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Desktop.Runtime;

namespace TinyHarness.Desktop.Tests;

/// <summary>
///     <para>
///         修改模型超时配置后的同模型刷新离线测试：真实的发送前刷新（DesktopSession.SubmitAsync）
///         与同模型重选（ChangeModelAsync）都必须识别超时策略变化并替换客户端，双向（有限 ↔ 0）
///         生效；替换必须发生在发送前——旧实例零请求且恰好释放一次，新实例实际收到请求；策略
///         未变时保持复用不产生修订。审批期限经 DesktopAutoApprovalProvider 在每次审核开始解析
///         并冻结，两次审核之间按"当前"值刷新。
///     </para>
///     <para>
///         Offline tests for same-model refreshes after the model timeout config changes: both the real
///         pre-send refresh (DesktopSession.SubmitAsync) and same-model reselection (ChangeModelAsync) must
///         detect the timeout-policy change and replace the client in both directions (bounded ↔ zero); the
///         replacement must happen before the send — the old instance sees zero requests and is released
///         exactly once while the new instance actually receives the request; an unchanged policy keeps reuse
///         without a revision. Through DesktopAutoApprovalProvider the approval deadline is resolved once per
///         review and frozen for that review, refreshing to the "current" value between reviews.
///     </para>
/// </summary>
[TestFixture]
public sealed class DesktopModelTimeoutRefreshTests
{
    /// <summary>
    ///     <para>
    ///         同模型重选路径：改配置后 ChangeModelAsync 必须替换客户端（双向），策略未变时保持
    ///         复用。该用例驱动选择路径，不经过发送；发送前刷新行为由 SubmitAsync 用例覆盖。
    ///     </para>
    ///     <para>
    ///         Same-model reselection path: after a config change ChangeModelAsync must replace the client
    ///         (both directions), and an unchanged policy keeps reuse. This case drives the selection path
    ///         without sending; pre-send refresh behavior is covered by the SubmitAsync cases.
    ///     </para>
    /// </summary>
    [Test]
    public async Task TimeoutPolicyChangeOnSameModelReselectionReplacesClientInBothDirectionsAndKeepsReuse()
    {
        var fixture = await Fixture.CreateAsync();
        var clients = new List<StubModel>();
        var factory = new DesktopSessionFactory((_, _) =>
                                                {
                                                    var client = new StubModel();
                                                    clients.Add(client);
                                                    return client;
                                                }, new Credentials(), fixture.ConfigPath);
        // 空闲 10 秒（区别于默认值）的初始策略：证明打开时记录的是本次配置采用的事实。
        var idleOnly = new ModelTimeouts { StreamIdleTimeoutSeconds = 10 };
        await UserConfigStore.SaveAsync(fixture.ConfigPath, Config(fixture, idleOnly), CancellationToken.None);
        await using var session = await factory.OpenAsync(
            new DesktopSessionRequest(fixture.Root,
                                      PermissionMode : SessionApprovalMode.HumanApproval,
                                      ProfileName    : "offline",
                                      ModelId        : "offline")
            {
                ProjectAssignmentState = SessionProjectAssignmentState.NoProject
            },
            _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask, CancellationToken.None);
        var host = session.Host;
        var opened = host.Session.ModelSelection!;
        Assert.Multiple(() =>
        {
            Assert.That(clients, Has.Count.EqualTo(1));
            Assert.That(host.EffectiveModelTimeouts, Is.EqualTo(idleOnly));
        });

        // 空闲 10 + 响应总期限 30：同模型下一次选择必须构造并采纳携带新策略的客户端。
        var bounded = new ModelTimeouts { StreamIdleTimeoutSeconds = 10, ResponseTimeoutSeconds = 30 };
        await UserConfigStore.SaveAsync(fixture.ConfigPath, Config(fixture, bounded), CancellationToken.None);
        var widened = await session.ChangeModelAsync("offline", "offline", null, opened.Revision);
        Assert.Multiple(() =>
        {
            Assert.That(clients, Has.Count.EqualTo(2));
            Assert.That(clients[0].DisposeCount, Is.EqualTo(1), "超时策略变化后旧客户端应随绑定替换释放");
            Assert.That(widened.Revision, Is.EqualTo(opened.Revision + 1));
            Assert.That(widened.ContinuationEpoch, Is.EqualTo(opened.ContinuationEpoch));
            Assert.That(host.EffectiveModelTimeouts, Is.EqualTo(bounded));
        });

        // 反向（30 → 0）：旧期限不得继续生效，同样必须替换。
        await UserConfigStore.SaveAsync(fixture.ConfigPath, Config(fixture, idleOnly), CancellationToken.None);
        var reverted = await session.ChangeModelAsync("offline", "offline", null, widened.Revision);
        Assert.Multiple(() =>
        {
            Assert.That(clients, Has.Count.EqualTo(3));
            Assert.That(clients[1].DisposeCount, Is.EqualTo(1));
            Assert.That(reverted.Revision, Is.EqualTo(widened.Revision + 1));
            Assert.That(host.EffectiveModelTimeouts, Is.EqualTo(idleOnly));
        });

        // 策略未变的同模型刷新保持快速复用：不构造新客户端，不产生修订。
        var unchanged = await session.ChangeModelAsync("offline", "offline", null, reverted.Revision);
        Assert.Multiple(() =>
        {
            Assert.That(clients, Has.Count.EqualTo(3), "策略未变时不应构造新客户端");
            Assert.That(clients[2].DisposeCount, Is.EqualTo(0));
            Assert.That(unchanged.Revision, Is.EqualTo(reverted.Revision));
        });
    }

    /// <summary>
    ///     <para>
    ///         真实的发送前刷新（双向）：改配置文件后 SubmitAsync 必须在发送前替换客户端——
    ///         旧实例恰好释放一次且请求计数为 0，新实例实际收到本次请求；反向同理；策略未变时
    ///         连续两次 SubmitAsync 复用同一实例。
    ///     </para>
    ///     <para>
    ///         The real pre-send refresh (both directions): after editing the config file, SubmitAsync must
    ///         replace the client before sending — the old instance is released exactly once with zero
    ///         requests while the new instance actually receives the request; the reverse direction alike;
    ///         with an unchanged policy two consecutive SubmitAsync calls reuse the same instance.
    ///     </para>
    /// </summary>
    [Test]
    public async Task TimeoutPolicyChangeOnRealSubmitRefreshesBindingBeforeSendInBothDirections()
    {
        var fixture = await Fixture.CreateAsync();
        var clients = new List<StubModel>();
        var factory = new DesktopSessionFactory((_, _) =>
                                                {
                                                    var client = new StubModel();
                                                    clients.Add(client);
                                                    return client;
                                                }, new Credentials(), fixture.ConfigPath);
        var idleOnly = new ModelTimeouts { StreamIdleTimeoutSeconds = 10 };
        await UserConfigStore.SaveAsync(fixture.ConfigPath, Config(fixture, idleOnly), CancellationToken.None);
        await using var session = await factory.OpenAsync(
            new DesktopSessionRequest(fixture.Root,
                                      PermissionMode : SessionApprovalMode.HumanApproval,
                                      ProfileName    : "offline",
                                      ModelId        : "offline")
            {
                ProjectAssignmentState = SessionProjectAssignmentState.NoProject
            },
            _ => ValueTask.CompletedTask, _ => ValueTask.CompletedTask, CancellationToken.None);
        var host = session.Host;
        Assert.Multiple(() =>
        {
            Assert.That(clients, Has.Count.EqualTo(1));
            Assert.That(host.EffectiveModelTimeouts, Is.EqualTo(idleOnly));
        });

        // 方向一（idle 10 → idle 10 + response 30）：发送前刷新替换客户端，旧实例零请求。
        var bounded = new ModelTimeouts { StreamIdleTimeoutSeconds = 10, ResponseTimeoutSeconds = 30 };
        await UserConfigStore.SaveAsync(fixture.ConfigPath, Config(fixture, bounded), CancellationToken.None);
        var first = await SubmitGuardedAsync(session, "first prompt");
        Assert.Multiple(() =>
        {
            Assert.That(first.Status, Is.EqualTo(AgentStatus.Completed), first.Error);
            Assert.That(clients, Has.Count.EqualTo(2));
            Assert.That(clients[0].DisposeCount, Is.EqualTo(1), "发送前刷新后旧客户端应随绑定替换恰好释放一次");
            Assert.That(clients[0].Requests, Is.EqualTo(0), "替换发生在发送前：旧实例不得收到任何请求");
            Assert.That(clients[1].Requests, Is.GreaterThanOrEqualTo(1), "新实例应实际收到本次发送的请求");
            Assert.That(host.EffectiveModelTimeouts, Is.EqualTo(bounded));
        });

        // 方向二（response 30 → 0）：旧期限不得继续生效，发送前同样替换。
        await UserConfigStore.SaveAsync(fixture.ConfigPath, Config(fixture, idleOnly), CancellationToken.None);
        var second = await SubmitGuardedAsync(session, "second prompt");
        Assert.Multiple(() =>
        {
            Assert.That(second.Status, Is.EqualTo(AgentStatus.Completed), second.Error);
            Assert.That(clients, Has.Count.EqualTo(3));
            Assert.That(clients[1].DisposeCount, Is.EqualTo(1));
            Assert.That(clients[1].Requests, Is.EqualTo(1), "上一实例只承载第一次发送");
            Assert.That(clients[2].Requests, Is.GreaterThanOrEqualTo(1));
            Assert.That(host.EffectiveModelTimeouts, Is.EqualTo(idleOnly));
        });

        // 策略未变：两次 SubmitAsync 复用同一实例，不构造新客户端。
        var third = await SubmitGuardedAsync(session, "third prompt");
        Assert.Multiple(() =>
        {
            Assert.That(third.Status, Is.EqualTo(AgentStatus.Completed), third.Error);
            Assert.That(clients, Has.Count.EqualTo(3), "策略未变时发送前刷新不得构造新客户端");
            Assert.That(clients[2].Requests, Is.EqualTo(2), "同一实例应继续承载后续发送");
            Assert.That(clients[2].DisposeCount, Is.EqualTo(0));
            Assert.That(host.EffectiveModelTimeouts, Is.EqualTo(idleOnly));
        });
    }

    /// <summary>
    ///     <para>
    ///         Desktop 审批接线：DesktopAutoApprovalProvider 的期限 provider 在每次审核开始解析
    ///         并冻结——第一次审核按开始时的 30 秒到期（期间"当前"值改为 300 不得延长它）；随后
    ///         的审核按新值 300 秒到期，旧 30 秒点不再触发。
    ///     </para>
    ///     <para>
    ///         The desktop approval wiring: DesktopAutoApprovalProvider's deadline provider is resolved and
    ///         frozen at each review start — the first review expires at the 30 seconds it started with
    ///         (raising the "current" value to 300 mid-review must not extend it); the following review
    ///         expires at the new 300 seconds, and the old 30-second point no longer fires.
    ///     </para>
    /// </summary>
    [Test]
    public async Task ApprovalDeadlineFrozenAtReviewStartAndRefreshedOnNextReview()
    {
        var clock = new ManualClock();
        var currentSeconds = 30;
        var gates = new Queue<GateReviewerClient>([new(), new()]);
        var provider = new DesktopAutoApprovalProvider(
            new DesktopApprovalProvider(_ => ValueTask.CompletedTask, value => value),
            () => SessionApprovalMode.AIAutoReview,
            _ => Task.FromResult<(IChatCompletionClient, string)>((gates.Dequeue(), "reviewer-model")),
            () => new ApprovalContext([]),
            _ => ValueTask.CompletedTask,
            () => TimeSpan.FromSeconds(currentSeconds),
            clock);
        var request = ApprovalRequest();

        // 先取出门上实例再启动审核：工厂同步出队，Peek 必须在调用前捕获当次审核的客户端。
        var firstGate = gates.Peek();
        var first = provider.PromptAsync(request, CancellationToken.None);
        await firstGate.WaitStartedAsync(TimeSpan.FromSeconds(15));
        currentSeconds = 300;
        clock.Advance(TimeSpan.FromSeconds(31));
        var firstResolution = await AwaitGuardedAsync(first);
        Assert.Multiple(() =>
        {
            Assert.That(firstResolution.Kind, Is.EqualTo(ApprovalResolutionKind.Unavailable));
            Assert.That(firstResolution.Reason, Does.Contain("approval deadline of 30 seconds"));
        });

        var secondGate = gates.Peek();
        var second = provider.PromptAsync(request, CancellationToken.None);
        await secondGate.WaitStartedAsync(TimeSpan.FromSeconds(15));
        clock.Advance(TimeSpan.FromSeconds(30));
        await AssertStillPendingAsync(second);
        clock.Advance(TimeSpan.FromSeconds(271));
        var secondResolution = await AwaitGuardedAsync(second);
        Assert.Multiple(() =>
        {
            Assert.That(secondResolution.Kind, Is.EqualTo(ApprovalResolutionKind.Unavailable));
            Assert.That(secondResolution.Reason, Does.Contain("approval deadline of 300 seconds"));
        });
    }

    // ---- SubmitAsync 守卫与请求构造 -------------------------------------------------------

    /// <summary>带 30 秒守卫提交一回合并返回终态结果，避免实现缺陷退化为无限等待。</summary>
    private static async Task<AgentResult> SubmitGuardedAsync(DesktopSession session, string text)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = await session.SubmitAsync(text, guard.Token);
        return run.Result ?? throw new InvalidOperationException("Run finished without a terminal result.");
    }

    /// <summary>守卫等待审批结论：15 秒内未完成即测试失败。</summary>
    private static async Task<ApprovalResolution> AwaitGuardedAsync(Task<ApprovalResolution> task)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            return await task.WaitAsync(guard.Token);
        }
        catch (OperationCanceledException) when (guard.Token.IsCancellationRequested)
        {
            Assert.Fail("15 秒测试守卫超时：被测实现未按预期完成。");
            throw;
        }
    }

    /// <summary>
    ///     <para>确认审批任务仍挂起：期限未到时审核不得完成；短暂延迟只为让错误完成有机会暴露。</para>
    ///     <para>
    ///         Confirms the approval task is still pending: before the deadline a review must not complete;
    ///         the short delay only gives a wrongly-early completion a chance to surface.
    ///     </para>
    /// </summary>
    private static async Task AssertStillPendingAsync(Task task)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.That(task.IsCompleted, Is.False, "审批期限尚未到，审核不应完成。");
    }

    private static ApprovalRequest ApprovalRequest() => new(
        new ToolPreparation
        {
            ToolName        = "shell",
            CallId          = "call-1",
            Arguments       = new JsonObject { ["command"] = "dotnet test" },
            Capability      = "process.execute",
            Summary         = "shell: dotnet test",
            ExecutionPolicy = "backend=windows-sandbox:test",
            TargetPaths     = [Path.GetTempPath()]
        },
        PermissionLevel.Level1,
        [GrantScope.SingleOperation],
        null,
        null);

    // ---- fixture 与桩 ----------------------------------------------------------------------

    private static UserConfig Config(Fixture fixture, ModelTimeouts modelTimeouts) => new()
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
        Settings = new UserConfigSettings
        {
            SessionStoreDirectory = fixture.Store,
            ModelTimeouts         = modelTimeouts
        }
    };

    private sealed record Fixture(string Root, string Store, string ConfigPath)
    {
        public static async Task<Fixture> CreateAsync()
        {
            var base_  = Path.Combine(Path.GetTempPath(),
                                      "tinyharness-desktop-timeout-refresh-" + Guid.NewGuid().ToString("N"));
            // 会话存储必须在工作区之外：工作区与其存储、配置互为兄弟目录。
            var result = new Fixture(Path.Combine(base_, "ws"),
                                     Path.Combine(base_, "store"),
                                     Path.Combine(base_, "config.json"));
            Directory.CreateDirectory(result.Root);
            await UserConfigStore.SaveAsync(result.ConfigPath, Config(result, new ModelTimeouts()),
                                            CancellationToken.None);
            return result;
        }
    }

    private sealed class Credentials : ICredentialStore
    {
        public bool    IsSupported                => true;
        public string? Read(string   targetName) => "fixture-secret-key";
        public void    Save(string   targetName, string secret) => throw new NotSupportedException();
        public bool    Delete(string targetName) => throw new NotSupportedException();
    }

    /// <summary>
    ///     <para>最小桩客户端：立即回一段文本并正常结束；统计收到的请求与释放次数供替换断言使用。</para>
    ///     <para>
    ///         The minimal stub client: answers with one text chunk and stops; counts received requests and
    ///         disposals for replacement assertions.
    ///     </para>
    /// </summary>
    private sealed class StubModel : IChatCompletionClient, IDisposable
    {
        public int Requests { get; private set; }

        public int DisposeCount { get; private set; }

        public bool Disposed => DisposeCount > 0;

        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(
            ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests++;
            await Task.Yield();
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.ContentDelta, ContentDelta = "stub" };
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "stop" };
        }

        public void Dispose() => DisposeCount++;
    }

    /// <summary>
    ///     <para>门控审核客户端：记录启动信号后挂起在门上，直到审批令牌触发（期限到期或取消）。</para>
    ///     <para>
    ///         The gated reviewer client: signals start then hangs on the gate until the approval token fires
    ///         (deadline expiry or cancellation).
    ///     </para>
    /// </summary>
    private sealed class GateReviewerClient : IChatCompletionClient
    {
        private readonly TaskCompletionSource _gate    = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(
            ChatCompletionRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            yield return new ChatStreamEvent
            {
                Kind         = ChatStreamEventKind.ContentDelta,
                ContentDelta = """{"decision":"allow","riskLevel":"low","userAuthorization":"high","reason":"ok"}"""
            };
            yield return new ChatStreamEvent { Kind = ChatStreamEventKind.End, FinishReason = "stop" };
        }

        public Task WaitStartedAsync(TimeSpan guard) =>
            _started.Task.WaitAsync(guard);
    }

    /// <summary>
    ///     <para>手动时钟：计时器到期由 Advance 按虚拟时间确定性触发，供审批期限用例离线驱动。</para>
    ///     <para>
    ///         The manual clock: timer expiry is driven deterministically in virtual time by Advance so
    ///         approval-deadline cases run offline.
    ///     </para>
    /// </summary>
    private sealed class ManualClock : TimeProvider
    {
        private readonly Lock _lock = new();
        private readonly List<Entry> _timers = [];
        private long _nowTicks;

        private sealed class Entry(ManualTimer timer, TimerCallback callback, object? state)
        {
            public ManualTimer Timer { get; } = timer;

            public TimerCallback Callback { get; } = callback;

            public object? State { get; } = state;

            public long DueTicks;
            public bool Fired;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_lock)
            {
                var entry = new Entry(new ManualTimer(this), callback, state)
                {
                    DueTicks = _nowTicks + dueTime.Ticks
                };
                _timers.Add(entry);
                return entry.Timer;
            }
        }

        /// <summary>推进虚拟时钟并同步触发所有到期的计时器回调。</summary>
        public void Advance(TimeSpan delta)
        {
            List<Entry> due;
            lock (_lock)
            {
                _nowTicks += delta.Ticks;
                due = _timers.Where(t => !t.Fired && t.DueTicks <= _nowTicks).ToList();
                due.ForEach(t => t.Fired = true);
            }

            foreach (var entry in due) entry.Callback(entry.State);
        }

        private bool Reschedule(ManualTimer timer, TimeSpan dueTime)
        {
            lock (_lock)
            {
                var entry = _timers.SingleOrDefault(t => t.Timer == timer);
                if (entry is null) return false;
                if (dueTime.Ticks < 0)
                {
                    // -1 表示解除计时器（CTS 解除到期源的用法）。
                    _timers.Remove(entry);
                    return true;
                }

                entry.Fired    = false;
                entry.DueTicks = _nowTicks + dueTime.Ticks;
                return true;
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (_lock)
            {
                var entry = _timers.SingleOrDefault(t => t.Timer == timer);
                if (entry is not null) _timers.Remove(entry);
            }
        }

        private sealed class ManualTimer(ManualClock owner) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => owner.Reschedule(this, dueTime);

            public void Dispose() => owner.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
