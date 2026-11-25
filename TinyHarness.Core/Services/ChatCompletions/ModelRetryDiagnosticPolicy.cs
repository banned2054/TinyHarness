#if DEBUG
using System.ClientModel.Primitives;
using System.Diagnostics;
using TinyHarness.Core.Models.Configuration;

namespace TinyHarness.Core.Services.ChatCompletions;

/// <summary>
///     <para>
///         仅 Debug 构建挂载的 SDK 管线策略（PerTry）：记录每次 HTTP 尝试与自动重试的状态码、耗时及异常类型，
///         不记录凭据、请求头、查询参数或正文。
///     </para>
///     <para>
///         Debug-only SDK pipeline policy (PerTry) that logs each HTTP attempt and automatic retry's
///         status code, elapsed duration, and exception type without recording credentials, headers,
///         query parameters, or bodies.
///     </para>
/// </summary>
internal sealed class ModelRetryDiagnosticPolicy(string model, ChatApiKind api) : PipelinePolicy
{
    private static readonly Type AttemptPropertyKey = typeof(ModelRetryDiagnosticPolicy);

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        var attempt   = NextAttempt(message);
        var method    = message.Request.Method;
        var path      = SafePath(message.Request.Uri);
        var stopwatch = Stopwatch.StartNew();
        LogStart(attempt, method, path);
        try
        {
            ProcessNext(message, pipeline, currentIndex);
            LogResponse(attempt, method, path, message.Response, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            LogError(attempt, method, path, ex, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        var attempt   = NextAttempt(message);
        var method    = message.Request.Method;
        var path      = SafePath(message.Request.Uri);
        var stopwatch = Stopwatch.StartNew();
        LogStart(attempt, method, path);
        try
        {
            await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
            LogResponse(attempt, method, path, message.Response, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            LogError(attempt, method, path, ex, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    private static int NextAttempt(PipelineMessage message)
    {
        var attempt = message.TryGetProperty(AttemptPropertyKey, out var value) && value is int count ? count + 1 : 1;
        message.SetProperty(AttemptPropertyKey, attempt);
        return attempt;
    }

    private static string SafePath(Uri? uri) => uri is null ? "-" : uri.AbsolutePath;

    private void LogStart(int attempt, string method, string path) =>
        ModelRequestDiagnosticLog.Event("http-attempt",
                                        $"phase=start api={api} model={model} attempt={attempt} retry={(attempt > 1 ? "true" : "false")} method={method} path={path}");

    private void LogResponse(int attempt, string method, string path, PipelineResponse? response, long elapsedMs)
    {
        var status = response is null ? "none" : response.Status.ToString();
        var retryAfter = response is not null && response.Headers.TryGetValue("Retry-After", out var value)
            ? $" retryAfter={value}"
            : string.Empty;
        ModelRequestDiagnosticLog.Event("http-attempt",
                                        $"phase=end api={api} model={model} attempt={attempt} retry={(attempt > 1 ? "true" : "false")} method={method} path={path} status={status} elapsedMs={elapsedMs}{retryAfter}");
    }

    private void LogError(int attempt, string method, string path, Exception ex, long elapsedMs) =>
        ModelRequestDiagnosticLog.Event("http-attempt",
                                        $"phase=error api={api} model={model} attempt={attempt} retry={(attempt > 1 ? "true" : "false")} method={method} path={path} error={ex.GetType().Name} elapsedMs={elapsedMs}");
}
#endif
