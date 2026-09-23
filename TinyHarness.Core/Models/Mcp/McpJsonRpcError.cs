namespace TinyHarness.Core.Models.Mcp;

/// <summary>
///     JSON-RPC 2.0 错误对象。本切片只使用标准错误码与 server-error 区间的 -32002，
///     不附带 data 扩展字段。
///     The JSON-RPC 2.0 error object. This slice only uses the standard codes plus -32002 from the
///     server-error range; no data extension field is emitted.
/// </summary>
public sealed record McpJsonRpcError
{
    public required int Code { get; init; }

    public required string Message { get; init; }
}
