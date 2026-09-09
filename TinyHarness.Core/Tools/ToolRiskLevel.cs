namespace TinyHarness.Core.Tools;

/// <summary>
/// 审批界面使用的调用风险提示等级；它不替代 Allow/Ask/Deny 权限决策。
/// Advisory risk level shown during approval; it does not replace the Allow/Ask/Deny decision.
/// </summary>
public enum ToolRiskLevel
{
    Standard,
    Elevated,
}
