using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
/// &lt;home&gt;\.sandbox\setup_error.json：setup 失败详情（snake_case 错误码 + 消息）。
/// 退出码本身不区分失败类型，必须读本文件。
///
/// &lt;home&gt;\.sandbox\setup_error.json: setup failure details (a snake_case
/// code plus message). The exit code alone does not classify the failure;
/// this file must be read.
/// </summary>
public sealed record SandboxSetupErrorReport
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}
