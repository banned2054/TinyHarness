using System.Diagnostics;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.Session;

namespace TinyHarness.Desktop.Runtime;

public sealed partial class DesktopSessionFactory
{
    /// <summary>
    ///     <para>按独立命名设置创建短生命周期客户端；失败保留待重试状态，不影响主运行。</para>
    ///     <para>Creates a short-lived client from independent title settings; failures remain pending without affecting runs.</para>
    /// </summary>
    private async Task GenerateTitleAsync(ChatSessionHost   host, Func<DesktopEvent, ValueTask> publish,
                                          CancellationToken cancellationToken)
    {
        try
        {
            if (!await host.PrepareTitleGenerationAsync(cancellationToken).ConfigureAwait(false)) return;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var user     = await LoadUserConfigAsync(deadline.Token).ConfigureAwait(false);
            var settings = user.Settings?.SessionTitles ?? new SessionTitleSettings();
            settings.Validate();
            var target = settings.UseCurrentModel
                ? FindTarget(user, host.Session.ModelSelection, host.Session.Model)
                : user.Profiles.Where(profile => profile.Name == settings.Profile)
                      .SelectMany(profile => profile.Models.Where(model => model.Id == settings.Model)
                                                    .Select(model => ((UserProfile Profile, UserProfileModel Model)?)(
                                                                profile, model)))
                      .SingleOrDefault();
            if (target is not { } selected)
                throw new InvalidOperationException("自动命名供应商或模型已不存在，请在通用设置中重新选择。");
            var config = ConfigFor(new TinyHarnessConfig { WorkspaceRoot = host.Session.WorkspaceRoot },
                                   selected.Profile, selected.Model);
            var client = CreateClient(config, selected.Profile, user.Settings?.Network, user.Settings?.ModelTimeouts,
                                      true);
            try
            {
                var name = await SessionTitleGenerator.GenerateAsync(client, selected.Model.Id,
                                                                     host.Session.Turns[0].UserInput, deadline.Token)
                                                      .ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                if (await host.TrySetGeneratedTitleAsync(name, deadline.Token).ConfigureAwait(false))
                    await publish(new DesktopEvent("title", null, name)).ConfigureAwait(false);
            }
            finally
            {
                (client as IDisposable)?.Dispose();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 关闭时保留待命名状态，下次发送再尝试。
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Session title generation for {0} remains pending: {1}", host.Session.Id,
                               Redact(exception.Message));
        }
    }
}
