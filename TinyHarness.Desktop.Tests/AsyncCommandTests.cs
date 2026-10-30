using NUnit.Framework;
using TinyHarness.Desktop.ViewModels;

namespace TinyHarness.Desktop.Tests;

[TestFixture]
public sealed class AsyncCommandTests
{
    [TestCase(true)]
    [TestCase(false)]
    public async Task NotificationFailureIsReportedAndCommandCanRunAgain(bool beforeExecution)
    {
        var        executions    = 0;
        var        notifications = 0;
        Exception? reported      = null;
        var command = new AsyncCommand(() =>
        {
            executions++;
            return Task.CompletedTask;
        }, ex => reported = ex);
        EventHandler handler = (_, _) =>
        {
            notifications++;
            if (notifications == (beforeExecution ? 1 : 2)) throw new IOException("notification failed");
        };
        command.CanExecuteChanged += handler;
        await command.ExecuteAsync();
        Assert.That(reported!.Message, Is.EqualTo("notification failed"));
        Assert.That(command.CanExecute(null), Is.True);
        Assert.That(executions, Is.EqualTo(beforeExecution ? 0 : 1));
        command.CanExecuteChanged -= handler;
        await command.ExecuteAsync();
        Assert.That(executions, Is.EqualTo(beforeExecution ? 1 : 2));
    }

    [Test]
    public async Task ErrorReporterFailureDoesNotEscapeAsyncVoidAndPreservesBothErrors()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncCommand(async () =>
        {
            await release.Task;
            throw new TaskCanceledException("operation failed");
        }, _ => throw new InvalidOperationException("reporter failed"));
        var previous = SynchronizationContext.Current;
        var context  = new CommandContext();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            command.Execute(null);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Assert.That(command.CanExecute(null), Is.False);
        release.TrySetResult();
        await context.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.That(context.Escaped, Is.Null);
        var failure = (AggregateException)command.LastError!;
        Assert.That(failure.Flatten().InnerExceptions.Select(ex => ex.Message),
                    Is.EqualTo(new[] { "operation failed", "reporter failed" }));
        Assert.That(command.CanExecute(null), Is.True);
    }

    private sealed class CommandContext : SynchronizationContext
    {
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception?           Escaped   { get; private set; }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            try
            {
                callback(state);
            }
            catch (Exception exception)
            {
                Escaped = exception;
            }
        }

        public override void OperationCompleted() => Completed.TrySetResult();
    }
}
