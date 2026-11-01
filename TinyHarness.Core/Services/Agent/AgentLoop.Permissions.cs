using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Permissions;

namespace TinyHarness.Core.Services.Agent;

public sealed partial class AgentLoop
{
    /// <summary>
    ///     <para>把实际权限作为本次请求的宿主事实注入，不改写持久化对话。</para>
    ///     <para>Injects effective permissions as host facts for this request without rewriting persisted conversation.</para>
    /// </summary>
    private IReadOnlyList<ChatMessage> PermissionModelView()
    {
        var messages = _context.BuildModelView();
        if (_permissions?.Mode is not { } mode) return messages;
        var notice = ChatMessage.System($"当前权限模式：{mode.Label()}。{mode.Description()} " +
                                        "权限模式只决定工具审批，不扩大用户的任务授权。项目外路径请明确提交；"           +
                                        "shell 的 executionScope=host 会请求宿主执行，普通模式仍须审批。拒绝后不得绕过。");
        return messages.Count == 0 ? [notice] : [messages[0], notice, .. messages.Skip(1)];
    }
}
