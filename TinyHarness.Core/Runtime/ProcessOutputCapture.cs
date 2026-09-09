using System.Text;

namespace TinyHarness.Core.Runtime;

internal interface IProcessOutputCapture
{
    Task ReadAsync(StreamReader reader, CancellationToken cancellationToken);

    CapturedProcessOutput Complete();

    void Discard();
}

/// <summary>
/// 把一个进程流持续写入本地临时 artifact，同时只在内存中保留有界的模型视图。
/// 已知 secret 在写入 artifact 和内存前进行跨读取块脱敏。
///
/// Streams one redirected process channel to a local temporary artifact while
/// retaining only a bounded model-facing view in memory. Known secrets are
/// redacted across read-buffer boundaries before either destination sees them.
/// </summary>
internal sealed class ProcessOutputCapture(
    string                artifactPath,
    int                   modelCharacterLimit,
    IReadOnlyList<string> secrets) : IProcessOutputCapture
{
    private readonly int                     _headLimit = modelCharacterLimit / 2;
    private readonly int                     _tailLimit = modelCharacterLimit - modelCharacterLimit / 2;
    private readonly StringBuilder           _view      = new(modelCharacterLimit);
    private readonly StreamingSecretRedactor _redactor  = new(secrets);
    private          bool                    _truncated;
    private          long                    _totalCharacters;

    public async Task ReadAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(artifactPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                                                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier : false));
        var             buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await AppendAsync(_redactor.Transform(buffer.AsSpan(0, read), final : false), writer,
                              cancellationToken)
               .ConfigureAwait(false);
        }

        await AppendAsync(_redactor.Transform(ReadOnlySpan<char>.Empty, final : true), writer,
                          cancellationToken)
           .ConfigureAwait(false);
    }

    public CapturedProcessOutput Complete()
    {
        if (!_truncated)
        {
            TryDeleteArtifact();
            return new CapturedProcessOutput(_view.ToString(), false, _totalCharacters, ArtifactPath : null);
        }

        var omitted = Math.Max(0, _totalCharacters - _view.Length);
        var content = _view.ToString(0, _headLimit)                                                    +
                      $"\n... [truncated {omitted} chars; full redacted output: {artifactPath}] ...\n" +
                      _view.ToString(_view.Length - _tailLimit, _tailLimit);
        return new CapturedProcessOutput(content, true, _totalCharacters, artifactPath);
    }

    public void Discard() => TryDeleteArtifact();

    private async Task AppendAsync(string text, StreamWriter writer, CancellationToken cancellationToken)
    {
        if (text.Length == 0)
        {
            return;
        }

        await writer.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
        _totalCharacters += text.Length;
        if (!_truncated)
        {
            _view.Append(text);
            if (_view.Length <= _headLimit + _tailLimit)
            {
                return;
            }

            _truncated = true;
            var removeCount = _view.Length - _headLimit - _tailLimit;
            _view.Remove(_headLimit, removeCount);
            return;
        }

        _view.Append(text);
        if (_view.Length > _headLimit + _tailLimit)
        {
            _view.Remove(_headLimit, _view.Length - _headLimit - _tailLimit);
        }
    }

    private void TryDeleteArtifact()
    {
        try
        {
            File.Delete(artifactPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stale temporary artifact is safer than masking the command result.
        }
    }

    /// <summary>
    /// 对任意长度的读取块做流式替换，并保留足够尾部来识别跨块 secret。
    /// </summary>
    private sealed class StreamingSecretRedactor
    {
        private const    string                Replacement = "[REDACTED]";
        private readonly IReadOnlyList<string> _secrets;
        private readonly int                   _maximumSecretLength;
        private          string                _pending = string.Empty;

        public StreamingSecretRedactor(IReadOnlyList<string> secrets)
        {
            _secrets = secrets.Where(value => !string.IsNullOrEmpty(value))
                              .Distinct(StringComparer.Ordinal)
                              .OrderByDescending(value => value.Length)
                              .ToArray();
            _maximumSecretLength = _secrets.Count == 0 ? 0 : _secrets.Max(value => value.Length);
        }

        public string Transform(ReadOnlySpan<char> input, bool final)
        {
            if (_secrets.Count == 0)
            {
                return input.ToString();
            }

            _pending += input.ToString();
            var processLimit = final ? _pending.Length : Math.Max(0, _pending.Length - _maximumSecretLength + 1);
            if (processLimit == 0)
            {
                return string.Empty;
            }

            var output = new StringBuilder(processLimit);
            var index  = 0;
            while (index < processLimit)
            {
                var secret = _secrets.FirstOrDefault(value => index + value.Length <= _pending.Length &&
                                                              _pending.AsSpan(index, value.Length)
                                                                      .SequenceEqual(value));
                if (secret is not null)
                {
                    output.Append(Replacement);
                    index += secret.Length;
                }
                else
                {
                    output.Append(_pending[index++]);
                }
            }

            _pending = _pending[index..];
            return output.ToString();
        }
    }
}

/// <summary>
/// 单个进程输出流的有界视图及完整 artifact 元数据。
/// </summary>
internal sealed record CapturedProcessOutput(
    string  Content,
    bool    Truncated,
    long    OriginalCharacterCount,
    string? ArtifactPath);
