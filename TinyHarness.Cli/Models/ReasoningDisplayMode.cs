namespace TinyHarness.Cli.Models;

/// <summary>
///     <para>CLI 思考展示方式；只影响终端，不改变模型请求或会话历史。</para>
///     <para>CLI reasoning display mode; affects only the terminal, not model requests or session history.</para>
/// </summary>
internal enum ReasoningDisplayMode
{
    Status,
    Content
}
