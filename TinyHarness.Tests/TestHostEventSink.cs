using TinyHarness.Core.Models.Context;
using TinyHarness.Core.Models.Session;
using TinyHarness.Core.Services.Session;

namespace TinyHarness.Tests;

/// <summary>
///     <para>收集全部宿主事件的测试 sink，供事件契约与生命周期断言使用。</para>
///     <para>Test sink collecting every host event, used by event-contract and lifecycle assertions.</para>
/// </summary>
internal sealed class TestHostEventSink : IHostEventSink
{
    public List<HostEvent> Events { get; } = [];

    public ValueTask PublishAsync(HostEvent @event, CancellationToken cancellationToken)
    {
        Events.Add(@event);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     <para>按到达顺序取出全部压缩事件；负载类型不符时测试失败。</para>
    ///     <para>Returns every compaction event in arrival order; a payload of the wrong type fails the test.</para>
    /// </summary>
    public List<ContextChange> Compactions()
    {
        return Events.Where(@event => @event.Kind == HostEventKind.CompactionApplied)
                     .Select(@event => TestAssert.TypeOf<ContextChange>(@event.Payload))
                     .ToList();
    }
}
