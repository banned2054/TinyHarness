using Microsoft.Extensions.AI;
using NUnit.Framework;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using TinyHarness.Core.Models.Agent;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Configuration;
using TinyHarness.Core.Services.ChatCompletions;
using TinyHarness.Core.Services.ChatCompletions.Configuration;
using TinyHarness.Core.Services.Persistence;
using TinyHarness.Core.Services.Runtime;
using TinyHarness.Desktop.Runtime;
using TinyHarness.Desktop.ViewModels;
using ChatMessage = TinyHarness.Core.Models.ChatCompletions.ChatMessage;

namespace TinyHarness.Desktop.Tests;

[TestFixture]
public sealed class DesktopSdkCancellationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    [TestCase("stop")]
    [TestCase("close")]
    [TestCase("timeout")]
    [TestCase("budget")]
    public async Task RealSdkSseReadSettlesAndPersistsCorrectTerminal(string action)
    {
        var root      = Path.Combine(Path.GetTempPath(), "tinyharness-sdk-cancel-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var store     = Path.Combine(root, "sessions");
        var config    = Path.Combine(root, "user.json");
        Directory.CreateDirectory(workspace);
        await UserConfigStore.SaveAsync(config, new UserConfig
        {
            DefaultProfile = "offline",
            Profiles =
            [
                new UserProfile
                {
                    Name = "offline", Endpoint = "https://offline.invalid/v1", ApiKeyCredentialTarget = "fixture",
                    DefaultModel = "offline",
                    Models = [new UserProfileModel { Id = "offline", ContextWindowTokens = 128_000 }]
                }
            ],
            Settings = new UserConfigSettings
            {
                SessionStoreDirectory = store,
                RunBudget             = new RunBudgetLimits { MaxWallClockSeconds = action == "budget" ? 1 : 0 }
            }
        }, CancellationToken.None);
        using var stream    = new WaitingSseStream();
        using var handler   = new SseHandler(stream);
        using var http      = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var transport = new HttpClientPipelineTransport(http);
        var factory = new DesktopSessionFactory((_, _) => new MicrosoftAiChatClient(
                                                 new ChatClient("offline",
                                                                new ApiKeyCredential("offline-fixture-key"),
                                                                new OpenAIClientOptions
                                                                {
                                                                    Endpoint =
                                                                        new Uri("https://offline.invalid/v1/"),
                                                                    Transport   = transport,
                                                                    RetryPolicy = new ClientRetryPolicy(0),
                                                                    NetworkTimeout = action == "timeout"
                                                                        ? TimeSpan.FromMilliseconds(250)
                                                                        : TimeSpan.FromSeconds(30)
                                                                }).AsIChatClient()), new Credentials(), config);
        await using var manager = new DesktopSessionManager();
        var page = new SessionViewModel(manager, factory, workspace, dispatch : callback =>
        {
            callback();
            return ValueTask.CompletedTask;
        });
        page.Draft = "offline SDK cancellation";
        await page.SendAsync();
        await stream.Waiting.Task.WaitAsync(TestTimeout);
        Task? close = null;
        if (action == "stop")
        {
            Assert.DoesNotThrow(() => page.CancelCommand.Execute(null));
            Assert.DoesNotThrow(() => page.CancelCommand.Execute(null));
        }
        else if (action == "close")
        {
            close = manager.DisposeAsync().AsTask();
        }

        await page.Completion.WaitAsync(TestTimeout);
        if (close is not null) await close.WaitAsync(TestTimeout);
        var expected = action == "timeout" ? "Failed" : action == "budget" ? "BudgetLimitReached" : "Cancelled";
        Assert.That(page.Status, Is.EqualTo(expected), page.Notice);
        Assert.That(page.IsRunning, Is.False);
        var partial = page.Items.OfType<ProcessGroupItemViewModel>().SelectMany(group => group.Items)
                          .OfType<MessageItemViewModel>().Single(message => message.Content == "partial answer");
        Assert.That(partial.IsInterrupted, Is.True);
        Assert.That(stream.ReadCancelled, Is.True);
        Assert.That(stream.ResponseDisposed, Is.True);
        Assert.That(handler.Requests, Is.EqualTo(1), "取消和超时均不得悄悄重试。 ");
        var saved = await SessionStoreReader.ReadAsync(store, page.Id!, CancellationToken.None);
        Assert.That(saved!.Turns.Single().Runs.Single().Status, Is.EqualTo(expected));
        if (action == "budget")
            Assert.That(saved.Turns.Single().Runs.Single().Budget!.LimitReason, Is.EqualTo("wall-clock"));
        if (action == "stop")
        {
            page.Draft = "continue after real SDK cancellation";
            await page.SendAsync();
            await page.Completion.WaitAsync(TestTimeout);
            Assert.That(page.Status, Is.EqualTo("Completed"), page.Notice);
            Assert.That(handler.Requests, Is.EqualTo(2));
        }

        TestContext.Out
                   .WriteLine($"action={action}; status={expected}; readCancelled={stream.ReadCancelled}; responseDisposed={stream.ResponseDisposed}; fixture={root}");
    }

    [Test]
    public async Task RealSdkWrapsTransportAbortAsTaskCancellationWithoutFabricatingStreamEnd()
    {
        using var stream    = new WaitingSseStream();
        using var http      = new HttpClient(new SseHandler(stream)) { Timeout = Timeout.InfiniteTimeSpan };
        using var transport = new HttpClientPipelineTransport(http);
        using var model = new MicrosoftAiChatClient(new ChatClient("offline",
                                                                   new ApiKeyCredential("offline-fixture-key"),
                                                                   new OpenAIClientOptions
                                                                   {
                                                                       Endpoint =
                                                                           new Uri("https://offline.invalid/v1/"),
                                                                       Transport      = transport,
                                                                       RetryPolicy    = new ClientRetryPolicy(0),
                                                                       NetworkTimeout = TimeSpan.FromSeconds(30)
                                                                   }).AsIChatClient());
        using var cancellation = new CancellationTokenSource();
        var       events       = new List<ChatStreamEvent>();
        var       read         = ConsumeAsync();
        await stream.Waiting.Task.WaitAsync(TestTimeout);
        cancellation.Cancel();
        var error = await Assert.CatchAsync<TaskCanceledException>(async () => await read.WaitAsync(TestTimeout));
        Assert.That(error!.StackTrace, Does.Contain("System.ClientModel.Internal.ReadTimeoutStream")
                                           .And.Contain("MicrosoftAiChatClient"));
        Assert.That(error.InnerException, Is.TypeOf<IOException>());
        Assert.That(error.InnerException!.InnerException, Is.TypeOf<SocketException>());
        Assert.That(events.Any(item => item.Kind == ChatStreamEventKind.End), Is.False);
        Assert.That(stream.ResponseDisposed, Is.True);
        TestContext.Out.WriteLine(error.ToString());

        async Task ConsumeAsync()
        {
            await foreach (var update in model.CompleteAsync(new ChatCompletionRequest
                           {
                               Model = "offline", Messages = [ChatMessage.User("offline")]
                           }, cancellation.Token)) events.Add(update);
        }
    }

    /// <summary>
    ///     <para>
    ///         停止时的取消异常必须始终由用户代码 await 浮出；<c>ValueTask.AsTask</c> 会先把它交给非用户
    ///         <c>ValueTaskSourceAsTask</c> catch 暂存，VS「仅我的代码」据此标记用户未处理并中断。
    ///     </para>
    ///     <para>
    ///         The stop cancellation must always surface at a user-code await; <c>ValueTask.AsTask</c> would hand
    ///         it to the non-user <c>ValueTaskSourceAsTask</c> catch first, which VS "Just My Code" reports as
    ///         unhandled by user code and breaks on.
    ///     </para>
    /// </summary>
    [Test]
    public async Task RealSdkStopCancellationSurfacesInUserCodeInsteadOfTaskWrapper()
    {
        var cancellationStacks = new List<string>();

        void OnFirstChance(object? sender, FirstChanceExceptionEventArgs args)
        {
            // 只记录类型与栈：first-chance 不区分最终处理方，栈中的包装帧才是断言对象。
            if (args.Exception is TaskCanceledException exception &&
                exception.StackTrace?.Contains("MicrosoftAiChatClient") == true)
                lock (cancellationStacks)
                {
                    cancellationStacks.Add(exception.StackTrace);
                }
        }

        var root      = Path.Combine(Path.GetTempPath(), "tinyharness-sdk-user-await-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var store     = Path.Combine(root, "sessions");
        var config    = Path.Combine(root, "user.json");
        Directory.CreateDirectory(workspace);
        await UserConfigStore.SaveAsync(config, new UserConfig
        {
            DefaultProfile = "offline",
            Profiles =
            [
                new UserProfile
                {
                    Name = "offline", Endpoint = "https://offline.invalid/v1", ApiKeyCredentialTarget = "fixture",
                    DefaultModel = "offline",
                    Models = [new UserProfileModel { Id = "offline", ContextWindowTokens = 128_000 }]
                }
            ],
            Settings = new UserConfigSettings { SessionStoreDirectory = store }
        }, CancellationToken.None);
        using var stream    = new WaitingSseStream();
        using var handler   = new SseHandler(stream);
        using var http      = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var transport = new HttpClientPipelineTransport(http);
        var factory = new DesktopSessionFactory((_, _) => new MicrosoftAiChatClient(
                                                 new ChatClient("offline",
                                                                new ApiKeyCredential("offline-fixture-key"),
                                                                new OpenAIClientOptions
                                                                {
                                                                    Endpoint =
                                                                        new Uri("https://offline.invalid/v1/"),
                                                                    Transport      = transport,
                                                                    RetryPolicy    = new ClientRetryPolicy(0),
                                                                    NetworkTimeout = TimeSpan.FromSeconds(30)
                                                                }).AsIChatClient()), new Credentials(), config);
        await using var manager = new DesktopSessionManager();
        var page = new SessionViewModel(manager, factory, workspace, dispatch : callback =>
        {
            callback();
            return ValueTask.CompletedTask;
        });
        page.Draft                                   =  "offline SDK user-code await";
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        try
        {
            await page.SendAsync();
            await stream.Waiting.Task.WaitAsync(TestTimeout);
            Assert.DoesNotThrow(() => page.CancelCommand.Execute(null));
            await page.Completion.WaitAsync(TestTimeout);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
        }

        Assert.That(page.Status, Is.EqualTo("Cancelled"), page.Notice);
        Assert.That(stream.ReadCancelled, Is.True);
        Assert.That(stream.ResponseDisposed, Is.True);
        Assert.That(cancellationStacks, Is.Not.Empty, "停止应产生穿过模型适配器的取消异常");
        Assert.That(cancellationStacks, Has.None.Contains("ValueTaskSourceAsTask"),
                    "取消异常经 ValueTask.AsTask 包装时由非用户 catch 暂存，VS 会标记为用户未处理");
    }

    private sealed class SseHandler(WaitingSseStream stream) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
                                                               CancellationToken  cancellationToken)
        {
            Requests++;
            Stream body = Requests == 1
                ? stream
                : new MemoryStream(Encoding.UTF8.GetBytes(
                                                          "data: {\"id\":\"offline\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"offline\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"offline answer\"},\"finish_reason\":null}]}\n\n" +
                                                          "data: {\"id\":\"offline\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"offline\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        }
    }

    private sealed class WaitingSseStream : Stream
    {
        private readonly byte[] _prefix =
            Encoding.UTF8
                    .GetBytes("data: {\"id\":\"offline\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"offline\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"partial answer\"},\"finish_reason\":null}]}\n\n");

        private         int                  _offset;
        public          TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public          bool                 ReadCancelled { get; private set; }
        public          bool                 ResponseDisposed { get; private set; }
        public override bool                 CanRead => true;
        public override bool                 CanSeek => false;
        public override bool                 CanWrite => false;
        public override long                 Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override Task<int>
            ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte>      buffer,
                                                       CancellationToken cancellationToken = default)
        {
            if (_offset < _prefix.Length)
            {
                var count = Math.Min(buffer.Length, _prefix.Length - _offset);
                _prefix.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }

            Waiting.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                ReadCancelled = true;
                // 模拟取消真实网络读取时的 IOException/SocketException，由 SDK 包装为取消异常。
                throw new IOException("offline transport interrupted",
                                      new SocketException((int)SocketError.OperationAborted));
            }

            return 0;
        }

        protected override void Dispose(bool disposing)
        {
            ResponseDisposed = true;
            base.Dispose(disposing);
        }

        public override void Flush()                                              => throw new NotSupportedException();
        public override int  Read(byte[]    buffer, int        offset, int count) => throw new NotSupportedException();
        public override long Seek(long      offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value)                         => throw new NotSupportedException();
        public override void Write(byte[]   buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Credentials : ICredentialStore
    {
        public bool    IsSupported                              => true;
        public string? Read(string   targetName)                => "offline-fixture-key";
        public void    Save(string   targetName, string secret) => throw new NotSupportedException();
        public bool    Delete(string targetName) => throw new NotSupportedException();
    }
}
