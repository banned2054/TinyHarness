#if DEBUG
using System.ClientModel;
using System.ClientModel.Primitives;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions;

namespace TinyHarness.Tests;

/// <summary>
///     <para>离线验证模型请求与 SDK 重试的 Debug 诊断日志：记录尝试次数与状态码且不泄露查询参数或凭据。</para>
///     <para>
///         Verifies the Debug diagnostic log for model requests and SDK retries offline: logs attempt counts
///         and status codes without leaking query parameters or credentials.
///     </para>
/// </summary>
[NonParallelizable]
public sealed class ModelRequestDiagnosticLogTests
{
    [TearDown]
    public void TearDown()
    {
        ModelRequestDiagnosticLog.ResetForTests();
        ModelRequestDiagnosticLog.LogDirectoryOverrideForTests = null;
    }

    [Test]
    public async Task RetryDiagnosticPolicy_LogsEachAttemptAndRedactsQueryParameters()
    {
        using var directory = new TestTempDir();
        ModelRequestDiagnosticLog.ResetForTests();
        ModelRequestDiagnosticLog.LogDirectoryOverrideForTests = directory.Root;
        ModelRequestDiagnosticLog.Initialize();

        var transport = new SequenceTransport(502, 200);
        var options   = new ClientPipelineOptions { Transport = transport };
        options.AddPolicy(new ModelRetryDiagnosticPolicy("test-model", ChatApiKind.Responses), PipelinePosition.PerTry);
        var pipeline = ClientPipeline.Create(options);

        using var message = pipeline.CreateMessage();
        message.Request.Method = "POST";
        message.Request.Uri    = new Uri("https://example.invalid/v1/responses?api-key=secret-token");
        message.Request.Headers.Set("Authorization", "Bearer top-secret-key");

        await pipeline.SendAsync(message);
        await ModelRequestDiagnosticLog.FlushForTestsAsync();

        var logPath = Directory.GetFiles(directory.Root, "model-request-diagnostic-*.log").Single();
        var content = await File.ReadAllTextAsync(logPath);

        Assert.That(message.Response!.Status, Is.EqualTo(200));
        Assert.That(content, Does.Contain("phase=start api=Responses model=test-model attempt=1 retry=false method=POST path=/v1/responses"));
        Assert.That(content, Does.Contain("phase=end api=Responses model=test-model attempt=1 retry=false method=POST path=/v1/responses status=502"));
        Assert.That(content, Does.Contain("phase=start api=Responses model=test-model attempt=2 retry=true method=POST path=/v1/responses"));
        Assert.That(content, Does.Contain("phase=end api=Responses model=test-model attempt=2 retry=true method=POST path=/v1/responses status=200"));
        Assert.That(content, Does.Not.Contain("secret-token"));
        Assert.That(content, Does.Not.Contain("top-secret-key"));
    }

    private sealed class SequenceTransport(params int[] statuses) : PipelineTransport
    {
        private int _index;

        protected override PipelineMessage CreateMessageCore() => new TestMessage();

        protected override void ProcessCore(PipelineMessage message)
        {
            var status = statuses[Math.Min(_index++, statuses.Length - 1)];
            ((TestMessage)message).SetResponse(new TestResponse(status));
        }

        protected override ValueTask ProcessCoreAsync(PipelineMessage message)
        {
            ProcessCore(message);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestMessage : PipelineMessage
    {
        public TestMessage() : base(new TestRequest())
        {
        }

        public void SetResponse(PipelineResponse response) => Response = response;
    }

    private sealed class TestRequest : PipelineRequest
    {
        private readonly TestHeaders _headers = new();

        protected override string              MethodCore  { get; set; } = "GET";
        protected override Uri?                UriCore     { get; set; }
        protected override BinaryContent?      ContentCore { get; set; }
        protected override PipelineRequestHeaders HeadersCore => _headers;

        public override void Dispose()
        {
        }
    }

    private sealed class TestResponse(int status) : PipelineResponse
    {
        private readonly TestResponseHeaders _headers = new();

        public override int                      Status         => status;
        public override string                   ReasonPhrase   => status.ToString();
        protected override PipelineResponseHeaders HeadersCore  => _headers;
        public override Stream?                  ContentStream  { get; set; }
        public override BinaryData               Content        => BinaryData.Empty;

        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => BinaryData.Empty;

        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(BinaryData.Empty);

        public override void Dispose()
        {
        }
    }

    private sealed class TestHeaders : PipelineRequestHeaders
    {
        private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);

        public override void Add(string name, string value)
        {
            if (!_values.TryGetValue(name, out var list))
            {
                list          = [];
                _values[name] = list;
            }

            list.Add(value);
        }

        public override void Set(string name, string value) => _values[name] = [value];

        public override bool Remove(string name) => _values.Remove(name);

        public override bool TryGetValue(string name, out string? value)
        {
            if (_values.TryGetValue(name, out var list) && list.Count > 0)
            {
                value = string.Join(",", list);
                return true;
            }

            value = null;
            return false;
        }

        public override bool TryGetValues(string name, out IEnumerable<string>? values)
        {
            if (_values.TryGetValue(name, out var list))
            {
                values = list;
                return true;
            }

            values = null;
            return false;
        }

        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
            _values.Select(pair => new KeyValuePair<string, string>(pair.Key, string.Join(",", pair.Value)))
                   .GetEnumerator();
    }

    private sealed class TestResponseHeaders : PipelineResponseHeaders
    {
        public override bool TryGetValue(string name, out string? value)
        {
            value = null;
            return false;
        }

        public override bool TryGetValues(string name, out IEnumerable<string>? values)
        {
            values = null;
            return false;
        }

        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
            Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();
    }
}
#endif
