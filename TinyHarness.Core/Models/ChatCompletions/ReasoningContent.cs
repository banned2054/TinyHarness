namespace TinyHarness.Core.Models.ChatCompletions;

/// <summary>
///     模型推理内容的承载。Text 是推理摘要文本；ProtectedData 是服务端加密的不透明
///     reasoning 载荷（Responses 协议的 encrypted_content）；ItemId 是服务端分配的
///     reasoning item id。三者需在后续请求中原样回传，才能续接 Responses 推理上下文。
///     Carries model reasoning. Text holds the reasoning summary; ProtectedData holds
///     the server-encrypted opaque reasoning payload (the Responses protocol's
///     encrypted_content); ItemId holds the server-assigned reasoning item id. All
///     three must be replayed verbatim in later requests to continue a Responses
///     reasoning context.
/// </summary>
public sealed record ReasoningContent(string? Text = null, string? ProtectedData = null, string? ItemId = null);
