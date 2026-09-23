namespace TinyHarness.Core.Models.Worker;

/// <summary>
///     一条可核查的证据引用。路径是工作区相对路径，行号从 1 开始且含端点；
///     行号反映生成结果时的文件内容，调用方应以当前文件重新核对。
///     One verifiable evidence citation. The path is workspace-relative and line numbers are 1-based and
///     inclusive; they reflect the file as it was when the result was produced, and callers should
///     re-check against the current file.
/// </summary>
public sealed record WorkerEvidence
{
    /// <summary>工作区相对路径，不以工作区根目录为前缀。 Workspace-relative path, never prefixed with the workspace root.</summary>
    public required string Path { get; init; }

    /// <summary>证据起始行（1 起）；为空表示整文件级别的引用。 First line (1-based); null means a whole-file reference.</summary>
    public int? LineStart { get; init; }

    /// <summary>
    ///     证据结束行（含）；仅与 <see cref="LineStart" /> 同时出现时有效。 Last line (inclusive); only meaningful together with
    ///     <see cref="LineStart" />.
    /// </summary>
    public int? LineEnd { get; init; }

    /// <summary>该证据与任务的关系说明。 Short note on why this evidence matters to the task.</summary>
    public string Note { get; init; } = string.Empty;
}
