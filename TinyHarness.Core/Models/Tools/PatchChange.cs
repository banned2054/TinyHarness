namespace TinyHarness.Core.Models.Tools;

/// <summary>
///     <para>apply_patch 产生的单个文件级变更事实；状态由执行结果决定。</para>
///     <para>One file-level change fact produced by apply_patch; its state is decided by execution.</para>
/// </summary>
public sealed record PatchChange(string Path, string Operation, string Diff, string State)
{
    public const string CreateOperation = "create";
    public const string ModifyOperation = "modify";
    public const string ConfirmedState  = "confirmed";
    public const string UnknownState    = "unknown";
}
