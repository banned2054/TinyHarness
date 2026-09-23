using TinyHarness.Core.Models.Worker;
using TinyHarness.Core.Services.Worker;

namespace TinyHarness.Tests;

public class WorkerRequestValidatorTests
{
    private static WorkerRequest MinimalValidRequest() => new()
    {
        TaskPrompt = "Find where the retry budget is enforced and report the call sites.",
    };

    // ---- valid requests ----------------------------------------------------

    [Fact]
    public void MinimalRequest_TaskOnly_IsValid()
    {
        var result = WorkerRequestValidator.Validate(MinimalValidRequest());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void CompleteRequest_WithAllOptionalFields_IsValid()
    {
        var request = new WorkerRequest
        {
            TaskPrompt     = "Explain how session snapshots are written.",
            KnownFacts     = ["Snapshots live under artifacts/runs.", "The CLI owns persistence."],
            FocusPaths     = ["src/persistence", "src/persistence/snapshot.cs"],
            ExpectedOutput = "A short summary plus up to three cited files.",
        };

        var result = WorkerRequestValidator.Validate(request);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
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
            ExpectedOutput = new string('b', WorkerRequestLimits.MaxExpectedOutputLength),
        };

        var result = WorkerRequestValidator.Validate(request);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    // ---- task --------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankTask_IsRejected(string taskPrompt)
    {
        var request = new WorkerRequest { TaskPrompt = taskPrompt };

        var result = WorkerRequestValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("'task'", StringComparison.Ordinal));
    }

    [Fact]
    public void NullTask_IsRejected()
    {
        var request = new WorkerRequest { TaskPrompt = null! };

        var result = WorkerRequestValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("'task'", StringComparison.Ordinal));
    }

    [Fact]
    public void TaskOverLimit_IsRejectedWithLimitAndActualLength()
    {
        var request = new WorkerRequest
        {
            TaskPrompt = new string('a', WorkerRequestLimits.MaxTaskPromptLength + 1),
        };

        var result = WorkerRequestValidator.Validate(request);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("'task'", error, StringComparison.Ordinal);
        Assert.Contains(WorkerRequestLimits.MaxTaskPromptLength.ToString(), error, StringComparison.Ordinal);
    }

    // ---- knownFacts ----------------------------------------------------------

    [Fact]
    public void KnownFacts_OverCount_IsRejected()
    {
        var request = MinimalValidRequest() with
        {
            KnownFacts = Enumerable.Range(1, WorkerRequestLimits.MaxKnownFactCount + 1)
                                   .Select(i => $"fact {i}")
                                   .ToArray(),
        };

        var result = WorkerRequestValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("'knownFacts'", StringComparison.Ordinal));
    }

    [Fact]
    public void KnownFact_OverLength_IsRejectedWithIndex()
    {
        var request = MinimalValidRequest() with
        {
            KnownFacts = ["short fact", new string('x', WorkerRequestLimits.MaxKnownFactLength + 1)],
        };

        var result = WorkerRequestValidator.Validate(request);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("knownFacts[1]", error, StringComparison.Ordinal);
        Assert.Contains(WorkerRequestLimits.MaxKnownFactLength.ToString(), error, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankKnownFact_IsRejectedWithIndex()
    {
        var request = MinimalValidRequest() with { KnownFacts = ["   "] };

        var result = WorkerRequestValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("knownFacts[0]", StringComparison.Ordinal));
    }

    // ---- focusPaths ----------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\src\\file.cs")]
    [InlineData("C:/src/file.cs")]
    [InlineData("C:relative.txt")]
    [InlineData("c:\\lower\\drive.cs")]
    [InlineData("/absolute/path.cs")]
    [InlineData("\\rooted.txt")]
    [InlineData("\\\\server\\share\\file.cs")]
    [InlineData("//host/share/file.cs")]
    [InlineData("../outside.txt")]
    [InlineData("src/../../outside.txt")]
    [InlineData("bad\u0001path.cs")]
    public void InvalidFocusPath_IsRejectedWithIndex(string focusPath)
    {
        var request = MinimalValidRequest() with { FocusPaths = [focusPath] };

        var result = WorkerRequestValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("focusPaths[0]", StringComparison.Ordinal));
    }

    [Fact]
    public void FocusPaths_OverCount_IsRejected()
    {
        var request = MinimalValidRequest() with
        {
            FocusPaths = Enumerable.Range(1, WorkerRequestLimits.MaxFocusPathCount + 1)
                                   .Select(i => $"dir/file{i}.cs")
                                   .ToArray(),
        };

        var result = WorkerRequestValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("'focusPaths'", StringComparison.Ordinal));
    }

    [Fact]
    public void FocusPath_OverLength_IsRejectedWithLimit()
    {
        var request = MinimalValidRequest() with
        {
            FocusPaths = [new string('p', WorkerRequestLimits.MaxFocusPathLength + 1)],
        };

        var result = WorkerRequestValidator.Validate(request);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("focusPaths[0]", error, StringComparison.Ordinal);
        Assert.Contains(WorkerRequestLimits.MaxFocusPathLength.ToString(), error, StringComparison.Ordinal);
    }

    // ---- expectedOutput --------------------------------------------------------

    [Fact]
    public void ExpectedOutput_OverLimit_IsRejectedWithLimit()
    {
        var request = MinimalValidRequest() with
        {
            ExpectedOutput = new string('b', WorkerRequestLimits.MaxExpectedOutputLength + 1),
        };

        var result = WorkerRequestValidator.Validate(request);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("'expectedOutput'", error, StringComparison.Ordinal);
        Assert.Contains(WorkerRequestLimits.MaxExpectedOutputLength.ToString(), error, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpectedOutput_Blank_IsTreatedAsAbsent()
    {
        var request = MinimalValidRequest() with { ExpectedOutput = "   " };

        var result = WorkerRequestValidator.Validate(request);

        Assert.True(result.IsValid);
    }

    // ---- request-level -------------------------------------------------------

    [Fact]
    public void NullRequest_IsInvalidWithoutThrowing()
    {
        var result = WorkerRequestValidator.Validate(null);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void MultipleInvalidFields_ReportAllErrors()
    {
        var request = new WorkerRequest
        {
            TaskPrompt     = " ",
            KnownFacts     = [new string('x', WorkerRequestLimits.MaxKnownFactLength + 1)],
            FocusPaths     = ["C:\\tmp"],
            ExpectedOutput = new string('y', WorkerRequestLimits.MaxExpectedOutputLength + 1),
        };

        var result = WorkerRequestValidator.Validate(request);

        Assert.False(result.IsValid);
        Assert.Equal(4, result.Errors.Count);
        Assert.Contains(result.Errors, error => error.Contains("'task'", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("knownFacts[0]", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("focusPaths[0]", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("expectedOutput", StringComparison.Ordinal));
    }
}
