using System.Diagnostics;
using System.Text;
using TinyHarness.Core.Services.Persistence;

namespace TinyHarness.Core.Services.ProjectInstructions;

/// <summary>
///     <para>项目规则日志的事件级别；Release 丢弃 Debug 级别。</para>
///     <para>Project-rule event levels; Release discards Debug events.</para>
/// </summary>
internal enum ProjectInstructionLogLevel
{
    Debug,
    Information,
    Warning,
    Error
}

/// <summary>
///     <para>项目规则元数据日志；Debug 保留检查详情，Release 仅保留重要事件，不参与执行或会话恢复。</para>
///     <para>
///         Project-rule metadata logging: Debug retains check details, Release retains important events only;
///         never execution or recovery state.
///     </para>
/// </summary>
public static class ProjectInstructionDiagnosticLog
{
    private const int MaximumLineCharacters = 32_768;
    private const int MaximumFileBytes = 8 * 1024 * 1024;
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object InitializationGate = new();
    private static readonly SecretRedactor DefaultRedactor = new(null);
    private static string? _path;
    private static bool _disabled;
    private static long _bytesWritten;
    private static long _sequence;

    internal static string? LogDirectoryOverrideForTests { get; set; }
    internal static string? FailureType { get; private set; }
    internal static bool IsEnabled => _path is not null && !_disabled;

    /// <summary>
    ///     <para>固定每进程日志路径；仅在首条事件时异步写入，库测试未显式启用时不访问真实用户目录。</para>
    ///     <para>
    ///         Pins a per-process log path; writes asynchronously on the first event, leaving user directories untouched
    ///         unless enabled.
    ///     </para>
    /// </summary>
    public static void Initialize()
    {
        lock (InitializationGate)
        {
            if (_path is not null) return;
            try
            {
                var directory = LogDirectoryOverrideForTests ??
                                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                             "tinyharness", "logs");
                _path = Path.Combine(directory,
                                     $"project-instructions-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-p{Environment.ProcessId}.log");
            }
            catch (Exception exception)
            {
                Disable(exception);
            }
        }
    }

    /// <summary>
    ///     <para>顺序追加已知密钥脱敏的元数据；单行和文件均有界，写入失败仅停用诊断并报告异常类型。</para>
    ///     <para>
    ///         Appends ordered, known-secret-redacted metadata with bounded lines and files; write failures disable
    ///         diagnostics and report only exception types.
    ///     </para>
    /// </summary>
    internal static async Task EventAsync(string metadata, SecretRedactor? redactor = null,
                                          ProjectInstructionLogLevel level = ProjectInstructionLogLevel.Information)
    {
#if !DEBUG
        if (level == ProjectInstructionLogLevel.Debug) return;
#endif
        var path = _path;
        if (path is null) return;
        await Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_disabled) return;
            redactor ??= DefaultRedactor;
            var header = _bytesWritten == 0 ? StartupHeader() : string.Empty;
            var line = $"{DateTime.UtcNow:O}|seq={_sequence++}|level={level}|{metadata}";
            var text = Flatten(redactor.Redact(line));
            if (text.Length > MaximumLineCharacters) text = text[..MaximumLineCharacters] + "...<truncated>";
            text = (header.Length == 0 ? string.Empty : Flatten(redactor.Redact(header)) + "\n") + text + "\n";
            var bytes = Encoding.UTF8.GetByteCount(text);
            if (_bytesWritten + bytes > MaximumFileBytes - 256)
            {
                text =
                    $"{DateTime.UtcNow:O}|level=Warning|event=log-limit|limit_bytes={MaximumFileBytes}|logging_stopped=true\n";
                _disabled = true;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.AppendAllTextAsync(path, text, Encoding.UTF8, CancellationToken.None).ConfigureAwait(false);
            _bytesWritten += Encoding.UTF8.GetByteCount(text);
        }
        catch (Exception exception)
        {
            // 诊断失败不能覆盖原来的工具结果；不把异常消息或 Data 中的任意内容写入其他出口。
            Disable(exception);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string StartupHeader()
    {
#if DEBUG
        const string build = "Debug";
#else
        const string build = "Release";
#endif
        return
            $"{DateTime.UtcNow:O}|level=Information|event=startup|pid={Environment.ProcessId}|build={build}|runtime={Environment.Version}|process={Environment.ProcessPath}";
    }

    private static string Flatten(string text) =>
        text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static void Disable(Exception exception)
    {
        _disabled = true;
        FailureType = exception.GetType().Name;
        Trace.WriteLine($"Project instruction diagnostics disabled: {FailureType}");
    }

    /// <summary>
    ///     <para>仅在测试没有活动写入时重置进程状态；每个测试须使用独立目录。</para>
    ///     <para>Resets process state only when tests have no active writes; each test must use a separate directory.</para>
    /// </summary>
    internal static void ResetForTests()
    {
        _path = null;
        _disabled = false;
        _bytesWritten = 0;
        _sequence = 0;
        FailureType = null;
        LogDirectoryOverrideForTests = null;
    }
}
