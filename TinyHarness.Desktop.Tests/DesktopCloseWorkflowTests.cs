using NUnit.Framework;
using TinyHarness.Desktop.Presentation;

namespace TinyHarness.Desktop.Tests;

[TestFixture]
public sealed class DesktopCloseWorkflowTests
{
    [Test]
    public async Task SuccessfulShutdownWaitsForCleanupAndExitsOnceWithoutConfirmation()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls   = 0;
        var exits   = 0;
        var workflow = new DesktopCloseWorkflow(() =>
        {
            calls++;
            return release.Task;
        }, _ => throw new AssertionException("Successful cleanup must not prompt."), () => exits++);
        var first = workflow.RequestCloseAsync();
        await workflow.RequestCloseAsync();
        Assert.That(exits, Is.Zero);
        release.SetResult();
        await first;
        await workflow.RequestCloseAsync();
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(exits, Is.EqualTo(1));
    }

    [Test]
    public async Task FailedShutdownCanConfirmAgainWithoutRepeatingCleanupOrOpeningConcurrentPrompts()
    {
        var failure = new IOException("fixture cleanup failure");
        var answer  = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls   = 0;
        var prompts = 0;
        var exits   = 0;
        var workflow = new DesktopCloseWorkflow(() =>
        {
            calls++;
            throw failure;
        }, exception =>
        {
            Assert.That(exception, Is.SameAs(failure));
            prompts++;
            return answer.Task;
        }, () => exits++);
        var first = workflow.RequestCloseAsync();
        await workflow.RequestCloseAsync();
        Assert.That(prompts, Is.EqualTo(1));
        answer.SetResult(false);
        await first;
        Assert.That(exits, Is.Zero);
        answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = workflow.RequestCloseAsync();
        answer.SetResult(true);
        await second;
        await workflow.RequestCloseAsync();
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(prompts, Is.EqualTo(2));
        Assert.That(exits, Is.EqualTo(1));
    }

    [Test]
    public async Task FailedConfirmationCanBeReopenedWithoutRepeatingCleanup()
    {
        var calls   = 0;
        var prompts = 0;
        var exits   = 0;
        var workflow = new DesktopCloseWorkflow(() =>
        {
            calls++;
            return Task.FromException(new IOException("cleanup failure"));
        }, _ => ++prompts == 1
            ? Task.FromException<bool>(new IOException("dialog failure"))
            : Task.FromResult(true), () => exits++);
        await Assert.CatchAsync<IOException>(async () => await workflow.RequestCloseAsync());
        await workflow.RequestCloseAsync();
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(exits, Is.EqualTo(1));
    }
}
