using TinyHarness.Core.Models.Worker;
using TinyHarness.Core.Services.Worker;

namespace TinyHarness.Tests;

public class WorkerRequestValidatorTests
{
    private static WorkerRequest MinimalValidRequest() => new()
    {
        TaskPrompt = "Find where the retry budget is enforced and report the call sites."
    };

    // ---- valid requests ----------------------------------------------------

    [Test]
    public void MinimalRequest_TaskOnly_IsValid()
    {
        var result = WorkerRequestValidator.Validate(MinimalValidRequest());

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }

    [Test]
    public void CompleteRequest_WithAllOptionalFields_IsValid()
    {
        var request = new WorkerRequest
        {
            TaskPrompt     = "Explain how session snapshots are written.",
            KnownFacts     = ["Snapshots live under artifacts/runs.", "The CLI owns persistence."],
            FocusPaths     = ["src/persistence", "src/persistence/snapshot.cs"],
            ExpectedOutput = "A short summary plus up to three cited files."
        };

        var result = WorkerRequestValidator.Validate(request);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }

    [Test]
    public void FieldsAtExactLimits_AreValid()
    {
        var request = new WorkerRequest
        {
            TaskPrompt = new string('a', WorkerRequestLimits.MaxTaskPromptLength),
            KnownFacts = Enumerable.Range(1, WorkerRequestLimits.MaxKnownFactCount)
                                   .Select(i => new string((char)('a' + i % 26),
                                                           WorkerRequestLimits.MaxKnownFactLength)).ToArray(),
            FocusPaths = Enumerable.Range(1, WorkerRequestLimits.MaxFocusPathCount)
                                   .Select(i => new string('p', WorkerRequestLimits.MaxFocusPathLength - 4) +
                                                $"/p{i:D2}").ToArray(),
            ExpectedOutput = new string('b', WorkerRequestLimits.MaxExpectedOutputLength)
        };

        var result = WorkerRequestValidator.Validate(request);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }

    // ---- task --------------------------------------------------------------


    [TestCase("")]
    [TestCase("   ")]
    public void BlankTask_IsRejected(string taskPrompt)
    {
        var request = new WorkerRequest { TaskPrompt = taskPrompt };

        var result = WorkerRequestValidator.Validate(request);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("'task'", StringComparison.Ordinal), result.Errors);
    }

    [Test]
    public void NullTask_IsRejected()
    {
        var request = new WorkerRequest { TaskPrompt = null! };

        var result = WorkerRequestValidator.Validate(request);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("'task'", StringComparison.Ordinal), result.Errors);
    }

    [Test]
    public void TaskOverLimit_IsRejectedWithLimitAndActualLength()
    {
        var request = new WorkerRequest
        {
            TaskPrompt = new string('a', WorkerRequestLimits.MaxTaskPromptLength + 1)
        };

        var result = WorkerRequestValidator.Validate(request);

        Assert.That(result.IsValid, Is.False);
        var error = TestAssert.Single(result.Errors);
        TestAssert.Contains("'task'", error, StringComparison.Ordinal);
        TestAssert.Contains(WorkerRequestLimits.MaxTaskPromptLength.ToString(), error, StringComparison.Ordinal);
    }

    // ---- knownFacts ----------------------------------------------------------

    [Test]
    public void KnownFacts_OverCount_IsRejected()
    {
        var request = MinimalValidRequest() with
        {
            KnownFacts = Enumerable.Range(1, WorkerRequestLimits.MaxKnownFactCount + 1)
                                   .Select(i => $"fact {i}")
                                   .ToArray()
        };

        var result = WorkerRequestValidator.Validate(request);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("'knownFacts'", StringComparison.Ordinal), result.Errors);
    }

    [Test]
    public void KnownFact_OverLength_IsRejectedWithIndex()
    {
        var request = MinimalValidRequest() with
        {
            KnownFacts = ["short fact", new string('x', WorkerRequestLimits.MaxKnownFactLength + 1)]
        };

        var result = WorkerRequestValidator.Validate(request);

        Assert.That(result.IsValid, Is.False);
        var error = TestAssert.Single(result.Errors);
        TestAssert.Contains("knownFacts[1]", error, StringComparison.Ordinal);
        TestAssert.Contains(WorkerRequestLimits.MaxKnownFactLength.ToString(), error, StringComparison.Ordinal);
    }

    [Test]
    public void BlankKnownFact_IsRejectedWithIndex()
    {
        var request = MinimalValidRequest() with { KnownFacts = ["   "] };

        var result = WorkerRequestValidator.Validate(request);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("knownFacts[0]", StringComparison.Ordinal), result.Errors);
    }

    // ---- focusPaths ----------------------------------------------------------


    [TestCase("")]
    [TestCase("   ")]
    [TestCase("C:\\src\\file.cs")]
    [TestCase("C:/src/file.cs")]
    [TestCase("C:relative.txt")]
    [TestCase("c:\\lower\\drive.cs")]
    [TestCase("/absolute/path.cs")]
    [TestCase("\\rooted.txt")]
    [TestCase("\\\\server\\share\\file.cs")]
    [TestCase("//host/share/file.cs")]
    [TestCase("../outside.txt")]
    [TestCase("src/../../outside.txt")]
    [TestCase("bad\u0001path.cs")]
    public void InvalidFocusPath_IsRejectedWithIndex(string focusPath)
    {
        var request = MinimalValidRequest() with { FocusPaths = [focusPath] };

        var result = WorkerRequestValidator.Validate(request);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("focusPaths[0]", StringComparison.Ordinal), result.Errors);
    }

    [Test]
    public void FocusPaths_OverCount_IsRejected()
    {
        var request = MinimalValidRequest() with
        {
            FocusPaths = Enumerable.Range(1, WorkerRequestLimits.MaxFocusPathCount + 1)
                                   .Select(i => $"dir/file{i}.cs")
                                   .ToArray()
        };

        var result = WorkerRequestValidator.Validate(request);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("'focusPaths'", StringComparison.Ordinal), result.Errors);
    }

    [Test]
    public void FocusPath_OverLength_IsRejectedWithLimit()
    {
        var request = MinimalValidRequest() with
        {
            FocusPaths = [new string('p', WorkerRequestLimits.MaxFocusPathLength + 1)]
        };

        var result = WorkerRequestValidator.Validate(request);

        Assert.That(result.IsValid, Is.False);
        var error = TestAssert.Single(result.Errors);
        TestAssert.Contains("focusPaths[0]", error, StringComparison.Ordinal);
        TestAssert.Contains(WorkerRequestLimits.MaxFocusPathLength.ToString(), error, StringComparison.Ordinal);
    }

    // ---- expectedOutput --------------------------------------------------------

    [Test]
    public void ExpectedOutput_OverLimit_IsRejectedWithLimit()
    {
        var request = MinimalValidRequest() with
        {
            ExpectedOutput = new string('b', WorkerRequestLimits.MaxExpectedOutputLength + 1)
        };

        var result = WorkerRequestValidator.Validate(request);

        Assert.That(result.IsValid, Is.False);
        var error = TestAssert.Single(result.Errors);
        TestAssert.Contains("'expectedOutput'", error, StringComparison.Ordinal);
        TestAssert.Contains(WorkerRequestLimits.MaxExpectedOutputLength.ToString(), error, StringComparison.Ordinal);
    }

    [Test]
    public void ExpectedOutput_Blank_IsTreatedAsAbsent()
    {
        var request = MinimalValidRequest() with { ExpectedOutput = "   " };

        var result = WorkerRequestValidator.Validate(request);

        Assert.That(result.IsValid, Is.True);
    }

    // ---- request-level -------------------------------------------------------

    [Test]
    public void NullRequest_IsInvalidWithoutThrowing()
    {
        var result = WorkerRequestValidator.Validate(null);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Is.Not.Empty);
    }

    [Test]
    public void MultipleInvalidFields_ReportAllErrors()
    {
        var request = new WorkerRequest
        {
            TaskPrompt     = " ",
            KnownFacts     = [new string('x', WorkerRequestLimits.MaxKnownFactLength + 1)],
            FocusPaths     = ["C:\\tmp"],
            ExpectedOutput = new string('y', WorkerRequestLimits.MaxExpectedOutputLength + 1)
        };

        var result = WorkerRequestValidator.Validate(request);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors.Count, Is.EqualTo(4));
        TestAssert.ContainsAll(error => error.Contains("'task'", StringComparison.Ordinal), result.Errors);
        TestAssert.ContainsAll(error => error.Contains("knownFacts[0]", StringComparison.Ordinal), result.Errors);
        TestAssert.ContainsAll(error => error.Contains("focusPaths[0]", StringComparison.Ordinal), result.Errors);
        TestAssert.ContainsAll(error => error.Contains("expectedOutput", StringComparison.Ordinal), result.Errors);
    }
}
