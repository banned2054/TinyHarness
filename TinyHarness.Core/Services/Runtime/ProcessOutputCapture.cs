using System.Text;
using TinyHarness.Core.Models.Runtime;

namespace TinyHarness.Core.Services.Runtime;

/// <summary>
///     <para>分块写入本地临时输出并保留有界视图；仅在调用方提供秘密集合时进行跨块脱敏。</para>
///     <para>Writes chunked local output with a bounded view, redacting across chunks only when the caller supplies secrets.</para>
/// </summary>
public interface IProcessOutputCapture
{
    /// <summary>
    ///     追加一个输出块。空块表示该流已结束：捕获器完成跨块脱敏的尾部冲刷，之后不再有块。
    ///     Appends one output chunk. An empty chunk marks the stream as ended: the
    ///     capture flushes the cross-chunk redaction tail and expects no further chunks.
    /// </summary>
    ValueTask AppendAsync(ReadOnlyMemory<char> chunk, CancellationToken cancellationToken);

    /// <summary>
    ///     结束捕获并返回有界视图；被裁剪时包含截断说明与完整 artifact 路径。
    ///     Finalizes the capture and returns the bounded view; when truncated it
    ///     includes the truncation notice and the full-artifact path.
    /// </summary>
    CapturedProcessOutput Complete();

    /// <summary>
    ///     丢弃本次捕获并删除已写入的临时 artifact。
    ///     Discards the capture and deletes any temporary artifact already written.
    /// </summary>
    void Discard();
}

/// <summary>
///     <see cref="IProcessOutputCapture" /> 的本地文件实现。
///     Local-file implementation of <see cref="IProcessOutputCapture" />.
/// </summary>
internal sealed class ProcessOutputCapture(string artifactPath, int modelCharacterLimit, IReadOnlyList<string> secrets)
    : IProcessOutputCapture
{
    private readonly int                     _headLimit = modelCharacterLimit / 2;
    private readonly StreamingSecretRedactor _redactor  = new(secrets);
    private readonly int                     _tailLimit = modelCharacterLimit - modelCharacterLimit / 2;
    private readonly StringBuilder           _view      = new(modelCharacterLimit);
    private          FileStream?             _artifactStream;
    private          StreamWriter?           _artifactWriter;
    private          long                    _totalCharacters;
    private          bool                    _truncated;

    public async ValueTask AppendAsync(ReadOnlyMemory<char> chunk, CancellationToken cancellationToken)
    {
        var text = _redactor.Transform(chunk.Span, chunk.IsEmpty);
        if (text.Length == 0) return;

        if (_artifactWriter is null)
        {
            _artifactStream = new FileStream(artifactPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                                             16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            _artifactWriter = new StreamWriter(_artifactStream,
                                               new UTF8Encoding(false));
        }

        await _artifactWriter.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
        _totalCharacters += text.Length;
        if (!_truncated)
        {
            _view.Append(text);
            if (_view.Length <= _headLimit + _tailLimit) return;

            _truncated = true;
            var removeCount = _view.Length - _headLimit - _tailLimit;
            _view.Remove(_headLimit, removeCount);
            return;
        }

        _view.Append(text);
        if (_view.Length > _headLimit + _tailLimit) _view.Remove(_headLimit, _view.Length - _headLimit - _tailLimit);
    }

    public CapturedProcessOutput Complete()
    {
        CloseArtifact();
        if (!_truncated)
        {
            TryDeleteArtifact();
            return new CapturedProcessOutput(_view.ToString(), false, _totalCharacters, null);
        }

        var omitted    = Math.Max(0, _totalCharacters - _view.Length);
        var outputKind = secrets.Count == 0 ? "full output" : "full redacted output";
        var content = _view.ToString(0, _headLimit)                                            +
                      $"\n... [truncated {omitted} chars; {outputKind}: {artifactPath}] ...\n" +
                      _view.ToString(_view.Length - _tailLimit, _tailLimit);
        return new CapturedProcessOutput(content, true, _totalCharacters, artifactPath);
    }

    public void Discard()
    {
        CloseArtifact();
        TryDeleteArtifact();
    }

    private void CloseArtifact()
    {
        _artifactWriter?.Dispose();
        _artifactWriter = null;
        _artifactStream = null;
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
    ///     对任意长度的读取块做流式替换，并保留足够尾部来识别跨块 secret。
    /// </summary>
    private sealed class StreamingSecretRedactor
    {
        private const string Replacement = "[REDACTED]";

        private readonly int _maximumSecretLength;

        private readonly IReadOnlyList<string> _secrets;

        private string _pending = string.Empty;

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
            if (_secrets.Count == 0) return input.ToString();

            _pending += input.ToString();
            var processLimit = final ? _pending.Length : Math.Max(0, _pending.Length - _maximumSecretLength + 1);
            if (processLimit == 0) return string.Empty;

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
