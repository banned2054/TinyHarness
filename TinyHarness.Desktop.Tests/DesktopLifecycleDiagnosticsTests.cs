using NUnit.Framework;
using TinyHarness.Desktop;

namespace TinyHarness.Desktop.Tests;

[TestFixture]
public sealed class DesktopLifecycleDiagnosticsTests
{
    [Test]
    public void TerminalEvidenceOmitsMessagesDataAndSourcePathsButKeepsExceptionFrames()
    {
        Exception captured;
        try
        {
            throw new IOException("fixture-secret /private/payload", new OperationCanceledException("fixture-secret"));
        }
        catch (Exception exception)
        {
            captured = exception;
        }

        captured.Data["private"] = "fixture-secret";
        var output = DesktopLifecycleDiagnostics.Format("unhandled", captured, true);
        Assert.That(output, Does.Contain("phase=unhandled").And.Contain("terminating=True"));
        Assert.That(output, Does.Contain("System.IO.IOException").And.Contain("System.OperationCanceledException"));
        Assert.That(output,
                    Does.Contain(nameof(TerminalEvidenceOmitsMessagesDataAndSourcePathsButKeepsExceptionFrames)));
        Assert.That(output, Does.Not.Contain("fixture-secret").And.Not.Contain("/private/payload"));
        Assert.That(output, Does.Not.Contain(".cs:").And.Not.Contain("C:\\Code"));
    }

    [Test]
    public void AggregateEvidenceIncludesSiblingExceptionTypesWithoutTheirMessages()
    {
        var error = new AggregateException(new IOException("private-first"),
                                           new InvalidOperationException("private-second"));
        var output = DesktopLifecycleDiagnostics.Format("window-cleanup-failed", error, false);
        Assert.That(output, Does.Contain("System.AggregateException").And.Contain("System.IO.IOException")
                                .And.Contain("System.InvalidOperationException"));
        Assert.That(output, Does.Not.Contain("private-first").And.Not.Contain("private-second"));
    }
}
