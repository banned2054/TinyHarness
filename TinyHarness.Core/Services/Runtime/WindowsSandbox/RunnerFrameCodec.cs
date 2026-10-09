using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace TinyHarness.Core.Services.Runtime.WindowsSandbox;

/// <summary>
///     runner 命令管道的帧编解码：4 字节小端 u32 长度前缀 + UTF-8 JSON，单帧上限 8 MiB，
///     外层 {"version":6,"type":…,"payload":{…}}，version 硬校验。所有发送端必须串行化整帧
///     写入，避免长度前缀与消息体交错。
///     Frame codec for the runner command pipe: a 4-byte little-endian u32 length
///     prefix plus UTF-8 JSON, an 8 MiB per-frame cap, and the
///     {"version":6,"type":…,"payload":{…}} envelope with a hard version check.
///     Every sender must serialize whole-frame writes so length prefixes never
///     interleave with message bodies.
/// </summary>
internal static class RunnerFrameCodec
{
    public const int MaxFrameLength     = 8 * 1024 * 1024;
    public const int IpcProtocolVersion = 6;

    /// <summary>
    ///     写一帧带 payload 的消息；payload 用 source-generated metadata 序列化。帧体超限抛
    ///     InvalidDataException。
    ///     Writes one framed message with a payload serialized through
    ///     source-generated metadata; frames exceeding the cap throw
    ///     InvalidDataException.
    /// </summary>
    public static async Task WriteFrameAsync<T>(
        Stream stream, string type, T payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(payload);

        var typeInfo    = GetTypeInfo<T>();
        var payloadJson = JsonSerializer.SerializeToUtf8Bytes(payload, typeInfo);
        await WriteEnvelopeAsync(stream, type, payloadJson, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     写一帧空控制消息（terminate/close_stdin）；payload 保留为空对象 {}，不省略。
    ///     Writes one empty control frame (terminate/close_stdin); the payload
    ///     stays present as an empty object and is never omitted.
    /// </summary>
    public static Task WriteControlFrameAsync(Stream stream, string type, CancellationToken cancellationToken)
    {
        return WriteEnvelopeAsync(stream, type, "{}"u8.ToArray(), cancellationToken);
    }

    /// <summary>
    ///     读一帧；干净 EOF（首字节前）返回 null，帧中间截断抛 EndOfStreamException，超长、
    ///     非法 JSON 或版本不符抛 InvalidDataException。
    ///     Reads one frame; a clean EOF before the first byte returns null.
    ///     Mid-frame truncation surfaces as EndOfStreamException, while oversized
    ///     lengths, malformed JSON, or a wrong version throw InvalidDataException.
    /// </summary>
    public static async Task<RunnerFrame?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var lengthPrefix = new byte[4];
        var firstByte    = await stream.ReadAsync(lengthPrefix.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
        if (firstByte == 0) return null;

        await stream.ReadExactlyAsync(lengthPrefix.AsMemory(1), cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(lengthPrefix);
        if (length > MaxFrameLength)
            throw new InvalidDataException($"Runner frame length {length} exceeds the {MaxFrameLength} byte limit.");

        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken).ConfigureAwait(false);
        return ParseEnvelope(body);
    }

    /// <summary>
    ///     解析帧 payload 为强类型记录；生产读循环直接按需取字段。
    ///     Parses a frame payload into a strongly typed record; the production
    ///     read loop reads fields on demand instead.
    /// </summary>
    public static T ParsePayload<T>(RunnerFrame frame) => frame.Payload.Deserialize(GetTypeInfo<T>())
                                                       ?? throw new
                                                              InvalidDataException($"Frame '{frame.Type}' carried a null payload.");

    /// <summary>
    ///     取 source-generated 的类型元数据；类型未注册时抛出带类型名的异常，而不是空引用。
    ///     Resolves the source-generated type metadata; an unregistered type
    ///     throws a named exception instead of a null reference.
    /// </summary>
    private static JsonTypeInfo<T> GetTypeInfo<T>() =>
        WindowsSandboxJsonContext.Default.Options.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
     ?? throw new
            InvalidOperationException($"The Windows sandbox JSON context does not contain metadata for type '{typeof(T).FullName}'.");

    private static async Task WriteEnvelopeAsync(
        Stream stream, string type, byte[] payloadJson, CancellationToken cancellationToken)
    {
        using var body = new MemoryStream(payloadJson.Length + 64);
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", IpcProtocolVersion);
            writer.WriteString("type", type);
            writer.WritePropertyName("payload");
            writer.WriteRawValue(payloadJson);
            writer.WriteEndObject();
        }

        var bodyBytes = body.ToArray();
        if (bodyBytes.Length > MaxFrameLength)
            throw new InvalidDataException($"Runner frame '{type}' exceeds the {MaxFrameLength} byte limit.");

        var lengthPrefix = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(lengthPrefix, (uint)bodyBytes.Length);
        await stream.WriteAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static RunnerFrame ParseEnvelope(byte[] body)
    {
        string     type;
        JsonObject payload;
        try
        {
            // Single parse: validate and read directly on the JsonObject tree.
            var root = JsonNode.Parse(body) as JsonObject
                    ?? throw new InvalidDataException("Runner frame body is not a JSON object.");

            // Must exist, be a JSON number, and decode as an Int32 equal to
            // the protocol version (non-integer numbers fail TryGetValue).
            if (root["version"] is not JsonValue versionValue       ||
                versionValue.GetValueKind() != JsonValueKind.Number ||
                !versionValue.TryGetValue<int>(out var version)     ||
                version != IpcProtocolVersion)
                throw new InvalidDataException($"Runner frame does not carry protocol version {IpcProtocolVersion}.");

            if (root["type"] is not { } typeNode || typeNode.GetValueKind() != JsonValueKind.String)
                throw new InvalidDataException("Runner frame is missing its 'type'.");

            type = typeNode.GetValue<string>();

            if (root["payload"] is not JsonObject payloadObject)
                throw new InvalidDataException($"Runner frame '{type}' payload is not a JSON object.");

            payload = payloadObject;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Runner frame body is not valid JSON.", ex);
        }

        return new RunnerFrame(type, payload);
    }
}

/// <summary>
///     已解析的 runner 帧：消息类型加保留为 JsonObject 的 payload。
///     A parsed runner frame: the message type plus the payload kept as a JsonObject.
/// </summary>
internal sealed record RunnerFrame(string Type, JsonObject Payload);
