using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TinyHarness.Desktop.Services;

/// <summary>
///     <para>按供应商基地址获取 OpenAI 兼容模型 ID；不调用生成接口或解析模型能力。</para>
///     <para>
///         Fetches OpenAI-compatible model IDs from a provider base URL without generation calls or capability
///         inference.
///     </para>
/// </summary>
internal static class ProviderModelDiscovery
{
    private static readonly HttpClient Client           = new(new HttpClientHandler { AllowAutoRedirect = false });
    private const           int        MaxResponseBytes = 4 * 1024 * 1024;

    internal static Task<IReadOnlyList<string>> FetchAsync(
        string endpoint, string? apiKey, CancellationToken cancellationToken) =>
        FetchAsync(Client, endpoint, apiKey, cancellationToken);

    /// <summary>
    ///     <para>只接受不带认证信息、查询参数或片段的 HTTP(S) 基地址，保留自定义路径。</para>
    ///     <para>Accepts HTTP(S) base URLs without user information, query parameters or fragments, preserving custom paths.</para>
    /// </summary>
    internal static Uri ModelsUri(string endpoint)
    {
        if (!Uri.TryCreate(endpoint.Trim().TrimEnd('/') + "/models", UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidDataException("请填写不含认证信息、查询参数或片段的 HTTP(S) API 基地址。");
        return uri;
    }

    /// <summary>
    ///     <para>请求受取消、超时和响应大小限制；错误不携带服务正文、密钥或完整地址。</para>
    ///     <para>Bounds requests by cancellation, timeout and response size; errors omit response bodies, keys and full URLs.</para>
    /// </summary>
    internal static async Task<IReadOnlyList<string>> FetchAsync(
        HttpClient client, string endpoint, string? apiKey, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(HttpMethod.Get, ModelsUri(endpoint));
        if (!string.IsNullOrWhiteSpace(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                                         .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidDataException($"获取模型失败（HTTP {(int)response.StatusCode}），请检查接口地址、密钥及模型列表权限。");
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new InvalidDataException("模型列表响应过大，请检查接口地址。");

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var       buffer = new MemoryStream();
        var             chunk  = new byte[8192];
        int             count;
        while ((count = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaxResponseBytes)
                throw new InvalidDataException("模型列表响应过大，请检查接口地址。");
            buffer.Write(chunk, 0, count);
        }

        using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
        if (document.RootElement.ValueKind != JsonValueKind.Object     ||
            !document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("接口未返回兼容的模型列表（需要 data 数组）。");
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
            var value = id.GetString()!;
            if (value.Length is > 0 and <= 200 && !value.Any(char.IsWhiteSpace) && !value.Any(char.IsControl))
                ids.Add(value);
        }

        return ids.ToArray();
    }
}
