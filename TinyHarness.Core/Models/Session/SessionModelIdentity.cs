using TinyHarness.Core.Models.Configuration;

namespace TinyHarness.Core.Models.Session;

/// <summary>
///     会话固定的模型身份：模型 ID、endpoint 与 Chat API 协议种类。仅用于标识与续聊兼容性
///     判断，绝不携带 API key 或其他凭据；凭据在每次启动时按引用重新解析。
///     The session's fixed model identity: model id, endpoint and Chat API protocol kind.
///     Used for identification and resume-compatibility decisions only; it never carries
///     an API key or any other credential — credentials are re-resolved by reference at
///     every startup.
/// </summary>
public sealed record SessionModelIdentity(string ModelId, string Endpoint, ChatApiKind ChatApi);
