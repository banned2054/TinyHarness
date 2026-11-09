namespace TinyHarness.Cli.Services;

/// <summary>
///     <para>按 scheme、host 与 port 比较 endpoint 来源；路径变化不算更换服务器。</para>
///     <para>Compares endpoint origins by scheme, host and port; a path change is not a new server.</para>
/// </summary>
internal static class EndpointOrigin
{
    /// <summary>
    ///     <para>判断两个 endpoint 是否共享来源；旧值缺失或任一无法解析时返回 false。</para>
    ///     <para>
    ///         Whether two endpoints share an origin; false when the previous value is missing or either cannot be
    ///         parsed.
    ///     </para>
    /// </summary>
    public static bool Same(string? existingEndpoint, string endpoint)
    {
        if (string.IsNullOrWhiteSpace(existingEndpoint)) return false;

        return Uri.TryCreate(existingEndpoint, UriKind.Absolute, out var existingUri) &&
               Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) &&
               string.Equals(existingUri.GetLeftPart(UriPartial.Authority),
                             endpointUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
    }
}
