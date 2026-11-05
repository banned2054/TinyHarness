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
                                        "权限模式只决定工具审批，不扩大用户的任务授权。项目外路径请明确提交；" +
                                        "shell 的 executionScope=host 会以 TinyHarness 进程身份、无 OS 沙箱隔离地在宿主执行，除完全开放模式外都必须审批，审批拒绝后不得绕过。" +
                                        "workspace 命令对受保护路径（如 .git）报 Permission denied 等沙箱边界错误时，不要原样重试：" +
                                        "先确认此前步骤没有留下不确定的副作用；仅当用户已明确授权对应操作时，才改用 executionScope=host 重新提交该命令并等待审批；" +
                                        "用户已拒绝、撤销或收窄授权的操作不得再次申请。");
        return messages.Count == 0 ? [notice] : [messages[0], notice, .. messages.Skip(1)];
    }
}
