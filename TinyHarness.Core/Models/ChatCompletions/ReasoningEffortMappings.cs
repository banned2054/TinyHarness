namespace TinyHarness.Core.Models.ChatCompletions;

/// <summary>
///     <para>
///         已实现的强度请求映射版本；选择记录保存选择时使用的映射标识，未知版本按契约拒绝续聊。
///     </para>
///     <para>
///         The implemented reasoning-effort request mapping version; selections record the mapping used when they
///         were made, and unknown versions refuse continuation by contract.
///     </para>
/// </summary>
/// <remarks>
///     <para>
///         当前唯一实现是适配器把 <see cref="ModelReasoningEffort" /> 映射为 M.E.AI ChatOptions 强度字段，
///         两种 Chat API 协议共用；强度变化不开启新连续性区间，仅显式重置或模型／路由变化才会。
///         映射语义变化时必须递增版本并定义旧记录的拒绝规则，不得用当前实现静默重解释旧选择。
///     </para>
///     <para>
///         The only implementation today maps <see cref="ModelReasoningEffort" /> onto the M.E.AI ChatOptions effort
///         field, shared by both chat API protocols; an effort change never opens a new continuity interval — only
///         an explicit reset or a model/route change does. Changing mapping semantics must bump the version and
///         define rejection rules for older records instead of silently reinterpreting them.
///     </para>
/// </remarks>
public static class ReasoningEffortMappings
{
    /// <summary>当前适配器实现的强度映射版本。The effort mapping version implemented by the current adapter.</summary>
    public const string CurrentVersion = "microsoft-ai-chat-options-effort/1";

    /// <summary>
    ///     <para>判断映射版本是否由当前实现识别；未知版本不得用于新记录，也不得解释旧记录。</para>
    ///     <para>Whether the mapping version is known to this implementation; unknown versions neither enter new records nor interpret old ones.</para>
    /// </summary>
    public static bool IsKnown(string? version) =>
        string.Equals(version, CurrentVersion, StringComparison.Ordinal);
}
