namespace TinyHarness.Core.Models.Runtime.WindowsSandbox;

/// <summary>
///     沙箱就绪状态检查结果：Ready 为 true 才能执行；否则 Problems 列出全部阻碍（组件缺失、
///     未初始化、版本不匹配等），fail closed，不自动修复。
///     Result of the sandbox readiness check: execution requires Ready == true;
///     otherwise Problems lists every blocker (missing components, incomplete
///     provisioning, version mismatch, …). Fail closed; nothing self-repairs.
/// </summary>
public sealed record WindowsSandboxStatus
{
    public required bool Ready { get; init; }

    public required IReadOnlyList<string> Problems { get; init; }

    public static WindowsSandboxStatus Ok()
    {
        return new WindowsSandboxStatus { Ready = true, Problems = [] };
    }

    public static WindowsSandboxStatus Blocked(IReadOnlyList<string> problems)
    {
        return new WindowsSandboxStatus { Ready = false, Problems = problems };
    }
}
