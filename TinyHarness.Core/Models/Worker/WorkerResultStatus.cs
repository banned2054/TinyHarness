using System.Text.Json.Serialization;

namespace TinyHarness.Core.Models.Worker;

/// <summary>
///     一次性 worker run 的终止状态。Completed 表示结论成立；其余状态都不得被呈现为成功结论。
///     Terminal state of a one-shot worker run. Completed means a conclusion was reached; every other
///     state must not be presented as a successful conclusion.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkerResultStatus>))]
public enum WorkerResultStatus
{
    /// <summary>调查完成并给出结论。 Investigation finished with a conclusion.</summary>
    Completed,

    /// <summary>
    ///     达到有界限制而停止，只交付已有证据，不伪造结论。
    ///     Stopped at a bounded limit; only existing evidence is returned, without a fabricated conclusion.
    /// </summary>
    Incomplete,

    /// <summary>模型、协议或其他运行错误导致 run 失败。 The run failed on a model, protocol or other runtime error.</summary>
    Failed,

    /// <summary>调用方或宿主在完成前取消了 run。 The run was cancelled before completion.</summary>
    Cancelled
}
