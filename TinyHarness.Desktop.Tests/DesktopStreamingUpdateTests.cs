using NUnit.Framework;
using TinyHarness.Desktop.Models;
using TinyHarness.Desktop.Runtime;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Tests;

[TestFixture]
public sealed class DesktopStreamingUpdateTests
{
    [Test]
    public async Task DeltasShareOneFrameAndBoundaryWaitsForOrderedPublication()
    {
        var frame   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batches = new List<DesktopEvent[]>();
        var batcher = new StreamingEventBatcher(updates =>
        {
            batches.Add(updates.ToArray());
            return ValueTask.CompletedTask;
        }, () =>
        {
            entered.TrySetResult();
            return frame.Task;
        });
        for (var index = 0; index < 1000; index++)
            await batcher.PublishAsync(new DesktopEvent("reasoning", "run", "x"));
        await batcher.PublishAsync(new DesktopEvent("text", "run", "answer"));
        await batcher.PublishAsync(new DesktopEvent("reasoning", "run", "tail"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(batches, Is.Empty);
        var boundary = batcher.PublishAsync(new DesktopEvent("state", "run", "saving")).AsTask();
        Assert.That(boundary.IsCompleted, Is.False);
        frame.SetResult();
        await boundary.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(batches, Has.Count.EqualTo(2));
        Assert.That(batches[0].Select(update => update.Kind), Is.EqualTo(new[] { "reasoning", "text", "reasoning" }));
        Assert.That(batches[0][0].Text, Is.EqualTo(new string('x', 1000)));
        Assert.That(batches[0][2].Text, Is.EqualTo("tail"));
        Assert.That(batches[1][0].Kind, Is.EqualTo("state"));
        await batcher.FlushAsync();
    }

    [Test]
    public async Task OversizedDeltaIsBoundedAndWaitsForAnInFlightFrameWithoutLosingText()
    {
        var entered       = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release       = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batches       = new List<DesktopEvent[]>();
        var active        = 0;
        var maximumActive = 0;
        var batcher = new StreamingEventBatcher(async updates =>
        {
            active++;
            maximumActive = Math.Max(maximumActive, active);
            entered.TrySetResult();
            await release.Task;
            batches.Add(updates.ToArray());
            active--;
        }, () => Task.CompletedTask);
        var original = new string('字', StreamingEventBatcher.MaximumBatchCharacters * 3 + 17);
        var pending  = batcher.PublishAsync(new DesktopEvent("reasoning", "run", original)).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(pending.IsCompleted, Is.False);
        release.SetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await batcher.FlushAsync();
        Assert.That(maximumActive, Is.EqualTo(1));
        Assert.That(batches.All(batch => batch.Sum(update => update.Text!.Length) <=
                                         StreamingEventBatcher.MaximumBatchCharacters), Is.True);
        Assert.That(string.Concat(batches.SelectMany(batch => batch).Select(update => update.Text)),
                    Is.EqualTo(original));
    }

    [Test]
    public async Task PublicationFailureRemainsObservableAtTheNextBoundary()
    {
        var batcher = new StreamingEventBatcher(_ => throw new InvalidOperationException("fixture display failure"),
                                                () => Task.CompletedTask);
        await batcher.PublishAsync(new DesktopEvent("text", "run", "tail"));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await batcher.FlushAsync());
        Assert.That(exception!.Message, Is.EqualTo("fixture display failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                                                                await batcher.PublishAsync(new DesktopEvent("state",
                                                                    "run", "finished")));
    }

    [Test]
    public async Task ResetAfterCompletionDiscardsFailedPendingTextAndAllowsTheNextRun()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batches = new List<DesktopEvent[]>();
        var fail    = true;
        var batcher = new StreamingEventBatcher(async updates =>
        {
            if (fail)
            {
                entered.TrySetResult();
                await release.Task;
                throw new InvalidOperationException("fixture display failure");
            }

            batches.Add(updates.ToArray());
        }, () => Task.CompletedTask);
        await batcher.PublishAsync(new DesktopEvent("text", "failed-run", "in flight"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await batcher.PublishAsync(new DesktopEvent("reasoning", "failed-run", "discard this pending tail"));
        Assert.Throws<InvalidOperationException>(() => batcher.ResetAfterCompletion());
        release.SetResult();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await batcher.FlushAsync());
        Assert.That(exception!.Message, Is.EqualTo("fixture display failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                                                                await batcher.PublishAsync(new DesktopEvent("text",
                                                                    "next-run",
                                                                    "not accepted before reset")));

        batcher.ResetAfterCompletion();
        fail = false;
        await batcher.PublishAsync(new DesktopEvent("reasoning", "next-run", "recovered reasoning"));
        await batcher.PublishAsync(new DesktopEvent("text", "next-run", "recovered answer"));
        await batcher.PublishAsync(new DesktopEvent("state", "next-run", "finished"));
        await batcher.FlushAsync();
        Assert.That(batches.SelectMany(batch => batch), Is.EqualTo(new[]
        {
            new DesktopEvent("reasoning", "next-run", "recovered reasoning"),
            new DesktopEvent("text", "next-run", "recovered answer"),
            new DesktopEvent("state", "next-run", "finished")
        }));
        batcher.ResetAfterCompletion();
    }

    [Test]
    public async Task RunIdsRemainSeparateAndThe128SegmentLimitAppliesOrderedBackpressure()
    {
        var frames = Enumerable.Range(0, 2)
                               .Select(_ => new TaskCompletionSource(TaskCreationOptions
                                                                        .RunContinuationsAsynchronously))
                               .ToArray();
        var entered = Enumerable.Range(0, 2)
                                .Select(_ =>
                                            new TaskCompletionSource(TaskCreationOptions
                                                                        .RunContinuationsAsynchronously))
                                .ToArray();
        var frameIndex = 0;
        var batches    = new List<DesktopEvent[]>();
        var batcher = new StreamingEventBatcher(updates =>
        {
            batches.Add(updates.ToArray());
            return ValueTask.CompletedTask;
        }, () =>
        {
            var index = frameIndex++;
            entered[index].SetResult();
            return frames[index].Task;
        });
        var expected = Enumerable.Range(0, 130)
                                 .Select(index => new DesktopEvent("text", $"run-{index % 2}", $"{index};"))
                                 .ToArray();
        for (var index = 0; index < 127; index++) await batcher.PublishAsync(expected[index]);
        await entered[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
        var atCapacity = batcher.PublishAsync(expected[127]).AsTask();
        Assert.That(atCapacity.IsCompleted, Is.False);
        Assert.That(batches, Is.Empty);
        frames[0].SetResult();
        await atCapacity.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(batches, Has.Count.EqualTo(1));
        Assert.That(batches[0], Has.Length.EqualTo(128));

        await batcher.PublishAsync(expected[128]);
        await batcher.PublishAsync(expected[129]);
        await entered[1].Task.WaitAsync(TimeSpan.FromSeconds(5));
        var flush = batcher.FlushAsync();
        Assert.That(flush.IsCompleted, Is.False);
        frames[1].SetResult();
        await flush.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(batches, Has.Count.EqualTo(2));
        Assert.That(batches[1], Has.Length.EqualTo(2));
        Assert.That(batches.SelectMany(batch => batch), Is.EqualTo(expected));
    }

    [Test]
    public async Task ContinuousDeltasPublishAcrossFramesWithoutWaitingForATerminalBoundary()
    {
        const int frameCount = 3;
        var frames = Enumerable.Range(0, frameCount)
                               .Select(_ => new TaskCompletionSource(TaskCreationOptions
                                                                        .RunContinuationsAsynchronously))
                               .ToArray();
        var frameEntered = Enumerable.Range(0, frameCount)
                                     .Select(_ => new TaskCompletionSource(TaskCreationOptions
                                                                              .RunContinuationsAsynchronously))
                                     .ToArray();
        var publicationEntered = Enumerable.Range(0, frameCount)
                                           .Select(_ => new TaskCompletionSource(TaskCreationOptions
                                                      .RunContinuationsAsynchronously))
                                           .ToArray();
        var publicationRelease = Enumerable.Range(0, frameCount)
                                           .Select(_ => new TaskCompletionSource(TaskCreationOptions
                                                      .RunContinuationsAsynchronously))
                                           .ToArray();
        var batches          = new List<DesktopEvent[]>();
        var frameIndex       = 0;
        var publicationIndex = 0;
        var active           = 0;
        var maximumActive    = 0;
        var batcher = new StreamingEventBatcher(async updates =>
        {
            var index = publicationIndex++;
            active++;
            maximumActive = Math.Max(maximumActive, active);
            batches.Add(updates.ToArray());
            publicationEntered[index].SetResult();
            await publicationRelease[index].Task;
            active--;
        }, () =>
        {
            var index = frameIndex++;
            frameEntered[index].SetResult();
            return frames[index].Task;
        });
        await batcher.PublishAsync(new DesktopEvent("text", "run", "frame-0;"));
        for (var index = 0; index < frameCount; index++)
        {
            await frameEntered[index].Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(batches, Has.Count.EqualTo(index));
            frames[index].SetResult();
            await publicationEntered[index].Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(batches[index], Is.EqualTo(new[] { new DesktopEvent("text", "run", $"frame-{index};") }));
            if (index + 1 < frameCount)
                await batcher.PublishAsync(new DesktopEvent("text", "run", $"frame-{index + 1};"));
            publicationRelease[index].SetResult();
        }

        await batcher.FlushAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(frameIndex, Is.EqualTo(frameCount));
        Assert.That(maximumActive, Is.EqualTo(1));
        Assert.That(string.Concat(batches.SelectMany(batch => batch).Select(update => update.Text)),
                    Is.EqualTo("frame-0;frame-1;frame-2;"));
    }

    [TestCase("", false, false)]
    [TestCase(" ", true, false)]
    [TestCase("\t\r\n", true, false)]
    [TestCase("正文", true, true)]
    public void BodyContentRetainsWhitespaceWhileProcessBodyRequiresVisibleText(
        string content, bool hasContent, bool hasProcessBody)
    {
        var recorded = new MessageItemViewModel(new ConversationMessage(1, "message", MessageRole.Assistant,
                                                                        content, DateTimeOffset.UnixEpoch));
        var streaming = MessageItemViewModel.CreateStreaming();
        streaming.AppendText(content);
        foreach (var message in new[] { recorded, streaming })
        {
            Assert.That(message.Content, Is.EqualTo(content));
            Assert.That(message.HasContent, Is.EqualTo(hasContent));
            Assert.That(message.HasProcessBody, Is.EqualTo(hasProcessBody));
            Assert.That(message.HasResponseMetadata, Is.EqualTo(hasProcessBody));
            Assert.That(message.UserDisplayText, Is.Null);
            Assert.That(message.SystemDisplayText, Is.Null);
        }
    }

    [Test]
    public void WhitespaceBodyOnlyNotifiesHasContentOnceAndVisibleTextEnablesProcessBody()
    {
        var message = MessageItemViewModel.CreateStreaming();
        var changes = new List<string?>();
        message.PropertyChanged += (_, change) => changes.Add(change.PropertyName);
        message.AppendText(string.Empty);
        Assert.That(changes, Is.Empty);
        message.AppendText(" \t");
        Assert.That(message.HasContent, Is.True);
        Assert.That(message.HasProcessBody, Is.False);
        Assert.That(changes.Count(name => name == nameof(MessageItemViewModel.HasContent)), Is.EqualTo(1));
        message.AppendText("\r\n");
        message.AppendText("answer");
        Assert.That(message.Content, Is.EqualTo(" \t\r\nanswer"));
        Assert.That(message.HasProcessBody, Is.True);
        Assert.That(message.HasResponseMetadata, Is.True);
        Assert.That(changes.Count(name => name == nameof(MessageItemViewModel.HasContent)), Is.EqualTo(1));
        Assert.That(changes.Count(name => name == nameof(MessageItemViewModel.HasProcessBody)), Is.EqualTo(1));
        Assert.That(changes.Count(name => name == nameof(MessageItemViewModel.HasResponseMetadata)), Is.EqualTo(1));
        Assert.That(changes, Does.Not.Contain(nameof(MessageItemViewModel.UserDisplayText)));
        Assert.That(changes, Does.Not.Contain(nameof(MessageItemViewModel.SystemDisplayText)));
    }

    [TestCase(MessageRole.User)]
    [TestCase(MessageRole.System)]
    [TestCase(MessageRole.Assistant)]
    public void RoleSpecificDisplayTextTracksAppendsWithoutNotifyingOtherRoles(MessageRole role)
    {
        var message = new MessageItemViewModel(new ConversationMessage(1, "message", role,
                                                                       "original", DateTimeOffset.UnixEpoch));
        var changes = new List<string?>();
        message.PropertyChanged += (_, change) => changes.Add(change.PropertyName);
        Assert.That(message.UserDisplayText, Is.EqualTo(role   == MessageRole.User ? "original" : null));
        Assert.That(message.SystemDisplayText, Is.EqualTo(role == MessageRole.System ? "original" : null));
        message.AppendText(" tail");
        Assert.That(message.Content, Is.EqualTo("original tail"));
        Assert.That(message.UserDisplayText, Is.EqualTo(role   == MessageRole.User ? "original tail" : null));
        Assert.That(message.SystemDisplayText, Is.EqualTo(role == MessageRole.System ? "original tail" : null));
        Assert.That(changes.Count(name => name == nameof(MessageItemViewModel.UserDisplayText)),
                    Is.EqualTo(role            == MessageRole.User ? 1 : 0));
        Assert.That(changes.Count(name => name == nameof(MessageItemViewModel.SystemDisplayText)),
                    Is.EqualTo(role            == MessageRole.System ? 1 : 0));
        Assert.That(changes, Does.Contain(nameof(MessageItemViewModel.Content)));
    }

    [TestCase("  **first** line\r\nsecond")]
    [TestCase("***a***")]
    [TestCase("  \r\ntext")]
    [TestCase("emoji 😀 中文 **思考**")]
    [TestCase("heading                                                                                 end")]
    public void IncrementalSummaryMatchesOriginalSemanticsAtEveryFragment(string text)
    {
        var message  = MessageItemViewModel.CreateStreaming();
        var original = string.Empty;
        foreach (var character in text)
        {
            original += character;
            message.AppendReasoning(character.ToString());
            var line     = original.Replace("**", string.Empty, StringComparison.Ordinal).Split('\n', 2)[0].Trim();
            var expected = line.Length > 80 ? line[..80] + "…" : line;
            Assert.That(message.ReasoningSummary, Is.EqualTo(string.IsNullOrWhiteSpace(original) ? null : expected));
            Assert.That(message.Reasoning, Is.EqualTo(original));
        }
    }

    [Test]
    public void CollapsedReasoningDoesNotPublishBodySnapshotsAndExpansionCatchesUp()
    {
        var message     = MessageItemViewModel.CreateStreaming();
        var bodyUpdates = 0;
        message.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(MessageItemViewModel.ReasoningDisplayText)) bodyUpdates++;
        };
        for (var index = 0; index < 1000; index++) message.AppendReasoning("thinking\n");
        Assert.That(bodyUpdates, Is.Zero);
        Assert.That(message.ReasoningDisplayText, Is.Null);
        message.ToggleReasoningCommand.Execute(null);
        Assert.That(message.ReasoningDisplayText, Is.EqualTo(string.Concat(Enumerable.Repeat("thinking\n", 1000))));
        message.AppendReasoning("tail");
        Assert.That(message.ReasoningDisplayText, Does.EndWith("tail"));
        Assert.That(bodyUpdates, Is.EqualTo(2));
        var projection = message.GetProcessReasoningProjection()!;
        message.AppendReasoning(" projected tail");
        Assert.That(projection.Reasoning, Is.EqualTo(message.Reasoning));
        Assert.That(projection.ReasoningDisplayText, Is.EqualTo(message.Reasoning));
    }
}
