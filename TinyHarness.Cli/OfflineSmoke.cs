using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Permissions;
using TinyHarness.Core.Tools;

namespace TinyHarness.Cli;

/// <summary>
/// 离线冒烟测试的脚本审批器。会话级批准唯一的 apply_patch 调用，并记录提示次数，
/// 使冒烟流程可以确认权限链路确实运行。
///
/// Scripted approval provider for the offline smoke: auto-approves the single
/// apply_patch call as a session grant and counts how many times it was asked,
/// so the smoke can assert the permission flow really ran.
/// </summary>
internal sealed class SmokeApprover : IApprovalProvider
{
    public int Prompts { get; private set; }

    /// <summary>
    /// 记录一次审批提示并返回会话级允许。
    /// Records one approval prompt and returns a session-wide grant.
    /// </summary>
    public Task<ApprovalAction> PromptAsync(ToolPreparation preparation, CancellationToken cancellationToken)
    {
        Prompts++;
        return Task.FromResult(ApprovalAction.AllowSession);
    }
}

/// <summary>
/// 保存离线冒烟固定数据的可释放临时工作区；创建与清理由自身负责，不依赖仓库布局。
///
/// Disposable temp workspace holding the offline smoke fixtures. The smoke verb
/// creates and cleans up these files itself, so a run never depends on the repo
/// layout or leaves anything behind.
/// </summary>
internal sealed class SmokeWorkspace : IDisposable
{
    /// <summary>
    /// 包装已经创建的临时工作区根目录。
    /// Wraps an already-created temporary workspace root.
    /// </summary>
    private SmokeWorkspace(string root) => Root = root;

    public string Root { get; }

    /// <summary>
    /// 创建唯一临时目录并写入确定性的冒烟测试文件；失败时自动清理已建内容。
    /// Creates a unique temp directory and deterministic fixtures, cleaning partial state if setup fails.
    /// </summary>
    public static async Task<SmokeWorkspace> CreateAsync(CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "tinyharness-smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspace = new SmokeWorkspace(root);
        try
        {
            await workspace.WriteAsync("sample.txt", "alpha needle\nbeta\ngamma needle", cancellationToken)
                           .ConfigureAwait(false);
            await workspace.WriteAsync("long.txt", new string('x', 20_000) + "\ntail", cancellationToken)
                           .ConfigureAwait(false);
            await workspace.WriteAsync("src/fixme.cs", "line1\nline2\nline3\n", cancellationToken)
                           .ConfigureAwait(false);
            return workspace;
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 在临时工作区内创建父目录并异步写入一个固定文件。
    /// Creates parent directories and asynchronously writes one fixture inside the temp workspace.
    /// </summary>
    private Task WriteAsync(string name, string content, CancellationToken cancellationToken)
    {
        var full   = Path.Combine(Root, name);
        var parent = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        return File.WriteAllTextAsync(full, content, cancellationToken);
    }

    /// <summary>
    /// 尽力递归清理临时工作区；清理失败不掩盖冒烟流程的真实结果。
    /// Best-effort removes the temp workspace without masking the smoke run's actual result.
    /// </summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive : true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of a temp directory.
        }
    }
}

/// <summary>
/// 离线脚本中的一步：描述本轮工具调用（或最终文本轮）及下一次请求时对结果的校验。
///
/// One step of the offline smoke script: the tool call to issue (or the final
/// plain-text turn) plus the check its own result must satisfy. The check runs
/// on the next model request, once the Agent loop has handed the result back.
/// </summary>
internal sealed class SmokeStep
{
    /// <summary>
    /// 本轮调用的工具名；最终纯文本轮为 <see langword="null"/>。
    /// Tool to call this turn; <see langword="null"/> for the final plain-text turn.
    /// </summary>
    public string? ToolName { get; init; }

    public string? ArgumentsJson { get; init; }

    /// <summary>
    /// 校验本步骤的工具结果，不匹配时抛出异常。
    /// Validates this step's tool result and throws on mismatch.
    /// </summary>
    public Action<string>? VerifyResult { get; init; }

    public bool IsTool => ToolName is not null;

    /// <summary>
    /// 为当前脚本步骤构造流式事件，并主动拆分较长参数以覆盖真实分片组装路径。
    /// Builds this step's stream and deliberately fragments longer arguments to exercise real accumulation.
    /// </summary>
    public IReadOnlyList<ChatStreamEvent> BuildEvents(int turn)
    {
        if (ToolName is null)
        {
            return
            [
                Content("Smoke ok: the Agent loop ran "),
                Content("list_files, search_text, read_file and apply_patch through the permission flow."),
                End(),
            ];
        }

        // Arguments are split across deltas (when long enough) to exercise the
        // same fragment assembly the real transport relies on.
        const string callId = "smoke_call";
        var          events = new List<ChatStreamEvent>();
        if (ArgumentsJson!.Length > 2)
        {
            var mid = ArgumentsJson.Length / 2;
            events.Add(new ChatStreamEvent
            {
                Kind                   = ChatStreamEventKind.ToolCallDelta,
                ToolCallIndex          = 0,
                ToolCallId             = callId,
                ToolCallFunctionName   = ToolName,
                ToolCallArgumentsDelta = ArgumentsJson[..mid],
            });
            events.Add(new ChatStreamEvent
            {
                Kind                   = ChatStreamEventKind.ToolCallDelta,
                ToolCallIndex          = 0,
                ToolCallArgumentsDelta = ArgumentsJson[mid..],
            });
        }
        else
        {
            events.Add(new ChatStreamEvent
            {
                Kind                   = ChatStreamEventKind.ToolCallDelta,
                ToolCallIndex          = 0,
                ToolCallId             = callId,
                ToolCallFunctionName   = ToolName,
                ToolCallArgumentsDelta = ArgumentsJson,
            });
        }

        events.Add(End());
        return events;
    }

    /// <summary>
    /// 创建文本增量事件。
    /// Creates a text-delta event.
    /// </summary>
    private static ChatStreamEvent Content(string chunk) => new()
    {
        Kind         = ChatStreamEventKind.ContentDelta,
        ContentDelta = chunk,
    };

    /// <summary>
    /// 创建正常结束流事件。
    /// Creates a normal end-of-stream event.
    /// </summary>
    private static ChatStreamEvent End() => new() { Kind = ChatStreamEventKind.End };
}

/// <summary>
/// 离线冒烟使用的脚本模型客户端。每次请求播放下一步，并在推进前确认上一工具结果已经
/// 正确回到消息历史；任何不匹配都会让 Agent Loop 以失败状态结束。
///
/// Scripted model client for the offline smoke. Each model request replays the
/// next script step; before every step after the first it verifies that the
/// Agent loop really handed the previous tool result back into the conversation
/// (the model-tool-model closure) and that the content matches what the tool
/// must have produced. Any mismatch throws, which the Agent loop surfaces as
/// <see cref="Agent.AgentStatus.Failed"/> and makes the smoke exit non-zero.
/// </summary>
internal sealed class SmokeScriptClient(IReadOnlyList<SmokeStep> steps) : IChatCompletionClient
{
    private int _turn;

    /// <summary>
    /// 校验上一轮闭环并异步产生当前脚本步骤的流事件。
    /// Verifies the previous model-tool-model closure and asynchronously yields the current scripted stream.
    /// </summary>
    public async IAsyncEnumerable<ChatStreamEvent> CompleteAsync(ChatCompletionRequest request,
                                                                 [EnumeratorCancellation]
                                                                 CancellationToken cancellationToken)
    {
        var turn = _turn++;
        if (turn >= steps.Count)
        {
            throw new InvalidOperationException($"Smoke script: unexpected model request #{turn + 1}.");
        }

        if (turn > 0)
        {
            var previousTool = steps[turn - 1];
            if (!previousTool.IsTool)
            {
                throw new InvalidOperationException("Smoke script: a plain-text turn preceded a tool turn.");
            }

            var lastResult = request.Messages.LastOrDefault(m => m.Role == ChatRole.Tool);
            if (lastResult is null)
            {
                throw new InvalidOperationException(
                                                    $"Smoke: model request #{turn + 1} has no tool result in history; the Agent loop " +
                                                    "did not hand the previous tool result back (loop closure broken).");
            }

            previousTool.VerifyResult?.Invoke(lastResult.Content);
        }

        foreach (var item in steps[turn].BuildEvents(turn))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }
}

/// <summary>
/// 包含有序脚本步骤和预期工具执行次数的离线冒烟计划。
/// Offline smoke plan containing ordered steps and the expected tool-execution count.
/// </summary>
internal sealed record SmokePlan(IReadOnlyList<SmokeStep> Steps, int ExpectedToolExecutions);

/// <summary>
/// 构造使用固定输入和预期结果的离线冒烟脚本。
///
/// Builds the offline smoke script from fixed inputs and expected results.
/// </summary>
internal static class SmokeScript
{
    /// <summary>
    /// 按 list、search、read、patch、再读的顺序建立完整工具闭环。
    /// Builds the full list/search/read/patch/read tool-closure sequence.
    /// </summary>
    public static SmokePlan Build()
    {
        var steps = new List<SmokeStep>
        {
            Tool("list_files", "{}", content =>
            {
                RequireContains(content, "sample.txt", "list_files");
                RequireContains(content, "long.txt", "list_files");
            }),
            Tool("search_text", """{"pattern":"needle","path":"sample.txt"}""", content =>
            {
                RequireContains(content, "sample.txt:1: alpha needle", "search_text");
                RequireContains(content, "sample.txt:3: gamma needle", "search_text");
                RequireContains(content, "(2 matches in 1 file)", "search_text");
            }),
            Tool("read_file", """{"path":"sample.txt"}""", content =>
                     RequireEqual(content, "alpha needle\nbeta\ngamma needle", "read_file sample.txt")),
            Tool("read_file", """{"path":"sample.txt","offset":2,"limit":1}""", content =>
                     RequireEqual(content, "beta\n... truncated before line 3; continue with offset=3",
                                  "read_file sample.txt offset/limit")),
            Tool("read_file", """{"path":"long.txt"}""", content =>
            {
                RequireContains(content, "... [line 1 truncated]", "read_file long.txt");
                if (!content.EndsWith("tail", StringComparison.Ordinal))
                {
                    throw Mismatch("read_file long.txt", content, "to end with 'tail' after the cut line");
                }
            }),
            Tool("apply_patch", ApplyPatchArguments(), content =>
            {
                RequireContains(content, "Applied patch to 1 file(s)", "apply_patch");
                RequireContains(content, "src/fixme.cs", "apply_patch");
            }),
            Tool("read_file", """{"path":"src/fixme.cs"}""", content =>
                     RequireEqual(content, "line1\nline2-fixed\nline3", "read_file patched fixme.cs")),
            new() // Final plain-text turn; terminates the loop.
        };

        return new SmokePlan(steps, steps.Count(s => s.IsTool));
    }

    /// <summary>
    /// 用 <see cref="JsonObject"/> 构造替换示例文件第二行的 apply_patch 参数，确保换行被正确转义。
    ///
    /// The apply_patch arguments: a unified diff that replaces line 2 of
    /// src/fixme.cs. Built through JsonObject so the embedded newlines are
    /// escaped exactly as a JSON string argument would be.
    /// </summary>
    private static string ApplyPatchArguments()
    {
        const string patch = """
            --- a/src/fixme.cs
            +++ b/src/fixme.cs
            @@ -2 +2 @@
            -line2
            +line2-fixed
            """;
        return new JsonObject { ["patch"] = patch }.ToJsonString();
    }

    /// <summary>
    /// 创建带工具名、参数和结果断言的脚本步骤。
    /// Creates a scripted tool step with its name, arguments, and result assertion.
    /// </summary>
    private static SmokeStep Tool(string name, string argumentsJson, Action<string> verify)
        => new()
        {
            ToolName      = name,
            ArgumentsJson = argumentsJson,
            VerifyResult  = verify,
        };

    /// <summary>
    /// 断言工具结果包含指定片段，否则抛出带上下文的冒烟错误。
    /// Requires a tool result to contain a fragment or throws a contextual smoke error.
    /// </summary>
    private static void RequireContains(string content, string expected, string what)
    {
        if (!content.Contains(expected, StringComparison.Ordinal))
        {
            throw Mismatch(what, content, $"to contain '{expected}'");
        }
    }

    /// <summary>
    /// 断言工具结果与预期文本完全一致。
    /// Requires a tool result to exactly equal the expected text.
    /// </summary>
    private static void RequireEqual(string content, string expected, string what)
    {
        if (!string.Equals(content, expected, StringComparison.Ordinal))
        {
            throw Mismatch(what, content, $"to equal '{expected}'");
        }
    }

    /// <summary>
    /// 创建包含有限结果预览的统一冒烟不匹配异常，避免错误输出无限增长。
    /// Creates a consistent mismatch exception with a bounded result preview.
    /// </summary>
    private static InvalidOperationException Mismatch(string what, string content, string expectation)
    {
        var shown = content.Length <= 300 ? content : content[..300] + "...";
        return new InvalidOperationException($"Smoke: {what} result expected {expectation}, but got: {shown}");
    }
}
