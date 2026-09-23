namespace TinyHarness.Core.Models.Worker;

/// <summary>
///     worker 结果的校验结果。校验失败时 <see cref="Errors" /> 逐条给出字段级原因，
///     全部通过时为空列表。
///     Validation outcome for a worker result. When invalid, <see cref="Errors" /> lists one field-level
///     reason per problem; it is empty when the result passes.
/// </summary>
public sealed record WorkerResultValidationResult
{
    public required IReadOnlyList<string> Errors { get; init; }

    public bool IsValid => Errors.Count == 0;
}
