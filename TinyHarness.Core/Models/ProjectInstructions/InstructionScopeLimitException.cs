namespace TinyHarness.Core.Models.ProjectInstructions;

/// <summary>
///     <para>工具范围的规则资源超限；候选未生效，可缩小范围后重新预检。</para>
///     <para>
///         Instruction resources exceeded for a tool scope; the candidate is inactive and a narrower scope may be
///         checked.
///     </para>
/// </summary>
internal sealed class InstructionScopeLimitException(string message) : IOException(message);
