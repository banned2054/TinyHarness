using TinyHarness.Desktop.Models;

namespace TinyHarness.Desktop.Utils;

/// <summary>
///     <para>提供不依赖 Avalonia 渲染服务的工具标题和状态文案。</para>
///     <para>Provides tool titles and status text without requiring Avalonia rendering services.</para>
/// </summary>
internal static class ToolCallLabels
{
    private static readonly Dictionary<string, string> Titles = new(StringComparer.Ordinal)
    {
        ["shell"]                  = "运行命令",
        ["read_file"]              = "读取文件",
        ["list_files"]             = "查看目录",
        ["search_text"]            = "搜索文本",
        ["apply_patch"]            = "应用补丁",
        ["work_items"]             = "更新工作项",
        ["bash"]                   = "运行命令",
        ["pwsh"]                   = "运行命令",
        ["read"]                   = "读取",
        ["read_image"]             = "读取图片",
        ["web_fetch"]              = "读取",
        ["web_search"]             = "搜索",
        ["grep"]                   = "搜索",
        ["glob"]                   = "搜索",
        ["write"]                  = "写入",
        ["edit"]                   = "编辑",
        ["run_code"]               = "代码",
        ["present"]                = "交付文件",
        ["todo_write"]             = "更新任务清单",
        ["ask_user_question"]      = "提问",
        ["subagent"]               = "创建子智能体",
        ["list_subagent_models"]   = "查看可用模型",
        ["list_agents"]            = "查看子智能体",
        ["send_message"]           = "发送消息",
        ["interrupt_agent"]        = "中断智能体",
        ["wait_agent"]             = "等待子智能体",
        ["spawn_teammate"]         = "创建队友",
        ["team_task_create"]       = "创建团队任务",
        ["team_task_get"]          = "读取团队任务",
        ["team_task_update"]       = "更新团队任务",
        ["team_task_list"]         = "查看团队任务",
        ["create_goal"]            = "创建目标",
        ["get_goal"]               = "查看目标",
        ["update_goal"]            = "更新目标",
        ["schedule_create"]        = "创建定时任务",
        ["schedule_list"]          = "查看定时任务",
        ["schedule_update"]        = "修改定时任务",
        ["schedule_delete"]        = "删除定时任务",
        ["job_list"]               = "查看后台任务",
        ["job_output"]             = "读取任务输出",
        ["job_kill"]               = "取消后台任务",
        ["terminal_open"]          = "创建终端",
        ["terminal_read"]          = "读取终端",
        ["terminal_list"]          = "查看终端",
        ["terminal_signal"]        = "发送终端信号",
        ["terminal_close"]         = "关闭终端",
        ["lsp"]                    = "查询代码符号",
        ["workflow"]               = "运行工作流",
        ["ralph"]                  = "运行循环工作流",
        ["session_event_read"]     = "读取事件",
        ["session_event_search"]   = "搜索事件",
        ["session_event_trace"]    = "追踪事件",
        ["session_search"]         = "搜索会话",
        ["session_trace"]          = "追踪会话",
        ["cordis_package_inspect"] = "查询 Cordis 环境",
        ["cordis_runtime_inspect"] = "查询 Cordis 环境",
        ["cordis_inspect_list"]    = "检查提供方",
        ["cordis_inspect_query"]   = "查询运行时",
        ["cordis_inspect_self"]    = "检查动态插件",
        ["cordis_run"]             = "运行 Cordis 插件",
        ["cordis_stop"]            = "停止 Cordis 插件",
        ["cordis_undefine"]        = "移除 Cordis 插件"
    };

    /// <summary>
    ///     <para>返回已知工具的本地化标题，未知名称使用通用标题。</para>
    ///     <para>Returns a localized title for known tools and a generic title for unknown names.</para>
    /// </summary>
    public static string GetTitle(string? name) =>
        name is not null && Titles.TryGetValue(name, out var title) ? title : "工具调用";

    /// <summary>
    ///     <para>判断工具名称是否存在本地化标题。</para>
    ///     <para>Determines whether a localized title exists for the tool name.</para>
    /// </summary>
    public static bool IsKnown(string? name) => name is not null && Titles.ContainsKey(name);

    /// <summary>
    ///     <para>根据工具名称和状态返回简短本地化状态文案。</para>
    ///     <para>Returns concise localized status text for a tool name and state.</para>
    /// </summary>
    public static string GetStatusText(string? name, ToolActivityStatus status)
    {
        var delivering = name == "present";
        return status switch
        {
            ToolActivityStatus.Running    => delivering ? "正在交付" : "运行中…",
            ToolActivityStatus.Succeeded  => delivering ? "已交付" : "已完成",
            ToolActivityStatus.Failed     => delivering ? "交付失败" : "失败",
            ToolActivityStatus.NotStarted => "未开始",
            ToolActivityStatus.Denied     => "已拒绝",
            ToolActivityStatus.Unknown    => "结果未知",
            _                             => string.Empty
        };
    }
}
