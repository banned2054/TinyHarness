using System.Text.Json.Serialization.Metadata;

namespace TinyHarness.Cli.Services;

/// <summary>
///     <para>保留 CLI 帧工具适配；受限管道及帧校验仅由 Core 实现。</para>
///     <para>Preserves CLI framing adapters; Core alone implements restricted pipes and frame validation.</para>
/// </summary>
internal static class WorkspaceGrantChannel
{
    internal static bool IsValidName(string name) => Core.Services.Runtime.WorkspaceGrantChannel.IsValidName(name);

    internal static Task WriteAsync<T>(Stream stream, T value, JsonTypeInfo<T> type, CancellationToken token) =>
        Core.Services.Runtime.WorkspaceGrantChannel.WriteAsync(stream, value, type, token);

    internal static Task<T> ReadAsync<T>(Stream stream, JsonTypeInfo<T> type, CancellationToken token) =>
        Core.Services.Runtime.WorkspaceGrantChannel.ReadAsync(stream, type, token);
}
