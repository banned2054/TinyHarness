using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Session;
using TinyHarness.Core.Services.Tools;

namespace TinyHarness.Cli.Services;

/// <summary>
///     <para>离线验证原生产物中的控制、工作项、预算和同日志恢复，不调用模型服务或机器沙箱。</para>
///     <para>
///         Checks native control, work items, budgets and same-log restoration offline, without model services or
///         machine sandboxing.
///     </para>
/// </summary>
internal static class M15OfflineSmoke
{
    internal static async Task RunAsync(
        string workspace, string directory, SessionModelIdentity identity, CancellationToken cancellationToken)
    {
        var options = new AgentOptions
        {
            Model = identity.ModelId, MaxAgentSteps = 8, DefaultToolTimeoutSeconds = 30, EnableWorkItems = true
        };
        var script = new SmokeScriptClient([
            new SmokeStep
            {
                ToolName      = "work_items",
                ArgumentsJson = """{"action":"update","title":"obsolete plan","status":"pending"}""",
                VerifyResult = text => Require(text.Contains("user amendment", StringComparison.Ordinal),
                                               "old plan was not deferred")
            },
            new SmokeStep
            {
                ToolName = "work_items",
                ArgumentsJson =
                    """{"action":"update","title":"current goal; preserve constraints","status":"completed"}""",
                VerifyResult = text => Require(text.Contains("claims, not verified", StringComparison.Ordinal),
                                               "work claim boundary missing")
            },
            new SmokeStep()
        ]);
        var    sink = new ControlSink();
        string sessionId;
        using (var host = new ChatSessionHost(workspace, identity, options, script, new ToolRegistry([]),
                                              "M15 offline smoke",
                                              events : sink,
                                              sessionWriterFactory : id => new SessionStoreWriter(directory, id)))
        {
            sink.Host = host;
            var result =
                (await host.SubmitTurnAsync("inspect a small task", cancellationToken).ConfigureAwait(false)).Result!;
            Require(result is { Status: AgentStatus.Completed, ToolExecutions: 1, Budget.ModelRequests: 3 },
                    $"control run failed: {result.Status} {result.Error}");
            Require(sink is { SawPause: true, SawResume: true } &&
                    host.Session.WorkItems.Count == 1 &&
                    host.PersistenceWarning is null,
                    "pause/resume, work state or persistence failed");
            sessionId = host.Session.Id;
        }

        var snapshot = await SessionStoreReader.ReadAsync(directory, sessionId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("M15 smoke log missing.");
        Require(snapshot.WorkItems.Single().Status == "completed" &&
                snapshot.Amendments.Single().CanonicalMessageIndex is not null,
                "work-item or amendment projection missing");
        var restored      = SessionResume.Restore(snapshot, workspace, identity, "M15 offline smoke");
        var budgetOptions = options with { Budget = new RunBudgetLimits { MaxModelRequests = 1 } };
        using (var writer = await SessionStoreWriter.OpenExistingAsync(directory, sessionId, null, cancellationToken)
                                                    .ConfigureAwait(false))
        using (var host = new ChatSessionHost(restored.Session, restored.Context, budgetOptions,
                                              new SmokeScriptClient([
                                                  new SmokeStep
                                                  {
                                                      ToolName = "work_items", ArgumentsJson = """{"action":"list"}"""
                                                  }
                                              ]),
                                              new ToolRegistry([]), "M15 offline smoke", sessionWriter : writer))
        {
            var result =
                (await host.SubmitTurnAsync("inspect saved work", cancellationToken).ConfigureAwait(false)).Result!;
            Require(result is { Status: AgentStatus.BudgetLimitReached, Budget.LimitReason: "model-requests" } &&
                    host.PersistenceWarning is null, "budget terminal or restored work state failed");
        }

        var completed = (await SessionStoreReader.ReadAsync(directory, sessionId, cancellationToken)
                                                 .ConfigureAwait(false))!;
        Require(completed.Turns[^1].Runs[^1].Budget?.LimitReason == "model-requests", "budget fact did not round-trip");
    }

    private static void Require(bool condition, string detail)
    {
        if (!condition) throw new InvalidDataException($"M15 offline smoke: {detail}.");
    }

    private sealed class ControlSink : IHostEventSink
    {
        private  bool             _amended;
        internal ChatSessionHost? Host      { get; set; }
        internal bool             SawPause  { get; private set; }
        internal bool             SawResume { get; private set; }

        public async ValueTask PublishAsync(HostEvent value, CancellationToken cancellationToken)
        {
            if (value.Kind == HostEventKind.ToolPrepared && !_amended)
            {
                _amended = true;
                Host!.RequestPause();
                await Host.QueueAmendmentAsync("keep only the current goal and its constraints", cancellationToken)
                          .ConfigureAwait(false);
            }

            if (value is { Kind: HostEventKind.RunControlChanged, Payload: "paused" })
            {
                SawPause = true;
                Host!.ResumeRun();
            }

            if (value is { Kind: HostEventKind.RunControlChanged, Payload: "running" }) SawResume = true;
        }
    }
}
