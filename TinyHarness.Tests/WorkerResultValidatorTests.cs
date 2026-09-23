using TinyHarness.Core.Models.Worker;
using TinyHarness.Core.Services.Worker;

namespace TinyHarness.Tests;

public class WorkerResultValidatorTests
{
    private static WorkerResult CompletedResult() => new()
    {
        Status     = WorkerResultStatus.Completed,
        Conclusion = "The retry budget is enforced in RetryPolicy.Evaluate.",
    };

    private static WorkerResult NonCompletedResult(WorkerResultStatus status) => new()
    {
        Status       = status,
        StatusDetail = "Stopped at the tool-call limit; no final conclusion was reached.",
    };

    // ---- legal shapes per status ---------------------------------------------

    [Fact]
    public void CompletedResult_WithConclusionOnly_IsValid()
    {
        var result = WorkerResultValidator.Validate(CompletedResult());

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void CompletedResult_WithFullContent_IsValid()
    {
        var request = CompletedResult() with
        {
            Evidence =
            [
                new WorkerEvidence
                {
                    Path      = "src/RetryPolicy.cs",
                    LineStart = 17,
                    LineEnd   = 29,
                    Note      = "The budget check happens here.",
                },
            ],
            SuggestedChanges = ["Move the budget check before the first attempt."],
            TestSuggestions  = ["Add a unit test that exhausts the budget on the first attempt."],
            Uncertainties    = ["Behavior on concurrent calls is unverified."],
            Statistics       = new WorkerExecutionStats
            {
                ModelRequests        = 3,
                ToolCalls            = 12,
                ToolOutputCharacters = 48_000,
                Elapsed              = TimeSpan.FromSeconds(90),
            },
        };

        var result = WorkerResultValidator.Validate(request);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData(WorkerResultStatus.Incomplete)]
    [InlineData(WorkerResultStatus.Failed)]
    [InlineData(WorkerResultStatus.Cancelled)]
    public void NonCompletedResult_WithStatusDetailOnly_IsValid(WorkerResultStatus status)
    {
        var result = WorkerResultValidator.Validate(NonCompletedResult(status));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    // ---- illegal status shapes -------------------------------------------------

    [Fact]
    public void CompletedResult_WithoutConclusion_IsRejected()
    {
        var candidate = CompletedResult() with { Conclusion = string.Empty };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("'conclusion'", StringComparison.Ordinal));
    }

    [Fact]
    public void CompletedResult_WithStatusDetail_IsRejected()
    {
        var candidate = CompletedResult() with { StatusDetail = "leftover note" };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("'statusDetail'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(WorkerResultStatus.Incomplete)]
    [InlineData(WorkerResultStatus.Failed)]
    [InlineData(WorkerResultStatus.Cancelled)]
    public void NonCompletedResult_WithConclusion_IsRejected(WorkerResultStatus status)
    {
        var candidate = NonCompletedResult(status) with { Conclusion = "Looks done to me." };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("'conclusion'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(WorkerResultStatus.Incomplete)]
    [InlineData(WorkerResultStatus.Failed)]
    [InlineData(WorkerResultStatus.Cancelled)]
    public void NonCompletedResult_WithoutStatusDetail_IsRejected(WorkerResultStatus status)
    {
        var candidate = NonCompletedResult(status) with { StatusDetail = "  " };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("'statusDetail'", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownStatusValue_IsRejected()
    {
        var candidate = CompletedResult() with { Status = (WorkerResultStatus)99 };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("'status'", StringComparison.Ordinal));
    }

    // ---- statistics --------------------------------------------------------------

    [Fact]
    public void NegativeModelRequests_IsRejected()
    {
        var candidate = CompletedResult() with { Statistics = new WorkerExecutionStats { ModelRequests = -1 } };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("statistics.modelRequests", StringComparison.Ordinal));
    }

    [Fact]
    public void NegativeToolCalls_IsRejected()
    {
        var candidate = CompletedResult() with { Statistics = new WorkerExecutionStats { ToolCalls = -1 } };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("statistics.toolCalls", StringComparison.Ordinal));
    }

    [Fact]
    public void NegativeToolOutputCharacters_IsRejected()
    {
        var candidate = CompletedResult() with
        {
            Statistics = new WorkerExecutionStats { ToolOutputCharacters = -1 },
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("statistics.toolOutputCharacters", StringComparison.Ordinal));
    }

    [Fact]
    public void NegativeElapsed_IsRejected()
    {
        var candidate = CompletedResult() with
        {
            Statistics = new WorkerExecutionStats { Elapsed = TimeSpan.FromSeconds(-1) },
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("statistics.elapsed", StringComparison.Ordinal));
    }

    [Fact]
    public void NullStatistics_IsRejectedWithoutThrowing()
    {
        var candidate = CompletedResult() with { Statistics = null! };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("'statistics'", StringComparison.Ordinal));
    }

    // ---- evidence ------------------------------------------------------------------

    [Fact]
    public void Evidence_AtExactCountLimit_IsValid()
    {
        var candidate = CompletedResult() with
        {
            Evidence = Enumerable.Range(1, WorkerResultLimits.MaxEvidenceCount)
                                 .Select(i => new WorkerEvidence
                                 {
                                     Path      = new string('p', WorkerResultLimits.MaxEvidencePathLength - 4) + $"/p{i:D2}",
                                     LineStart = 1,
                                     Note      = new string('n', WorkerResultLimits.MaxEvidenceNoteLength),
                                 })
                                 .ToArray(),
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Evidence_OverCount_IsRejected()
    {
        var candidate = CompletedResult() with
        {
            Evidence = Enumerable.Range(1, WorkerResultLimits.MaxEvidenceCount + 1)
                                 .Select(i => new WorkerEvidence { Path = $"src/file{i}.cs" })
                                 .ToArray(),
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("'evidence'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\src\\file.cs")]
    [InlineData("C:/src/file.cs")]
    [InlineData("C:relative.txt")]
    [InlineData("c:\\lower\\drive.cs")]
    [InlineData("/absolute/file.cs")]
    [InlineData("\\rooted.txt")]
    [InlineData("\\\\server\\share\\file.cs")]
    [InlineData("//host/share/file.cs")]
    [InlineData("../outside.txt")]
    [InlineData("src/../../outside.txt")]
    [InlineData("bad\u0001path.cs")]
    public void EvidencePath_InvalidShape_IsRejectedWithIndex(string path)
    {
        var candidate = CompletedResult() with { Evidence = [new WorkerEvidence { Path = path }] };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("evidence[0].path", StringComparison.Ordinal));
    }

    [Fact]
    public void EvidenceLineNumbers_ValidCombinations_AreAccepted()
    {
        var candidate = CompletedResult() with
        {
            Evidence =
            [
                new WorkerEvidence { Path = "src/a.cs" },
                new WorkerEvidence { Path = "src/b.cs", LineStart = 42 },
                new WorkerEvidence { Path = "src/c.cs", LineStart = 42, LineEnd = 42 },
                new WorkerEvidence { Path = "src/d.cs", LineStart = 1, LineEnd = 10 },
            ],
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void EvidenceLineStart_BelowOne_IsRejected(int lineStart)
    {
        var candidate = CompletedResult() with
        {
            Evidence = [new WorkerEvidence { Path = "src/a.cs", LineStart = lineStart }],
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("evidence[0].lineStart", StringComparison.Ordinal));
    }

    [Fact]
    public void EvidenceLineEnd_SmallerThanLineStart_IsRejected()
    {
        var candidate = CompletedResult() with
        {
            Evidence = [new WorkerEvidence { Path = "src/a.cs", LineStart = 10, LineEnd = 5 }],
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("evidence[0].lineEnd", StringComparison.Ordinal));
    }

    [Fact]
    public void EvidenceLineEnd_WithoutLineStart_IsRejected()
    {
        var candidate = CompletedResult() with
        {
            Evidence = [new WorkerEvidence { Path = "src/a.cs", LineEnd = 5 }],
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("evidence[0].lineEnd", StringComparison.Ordinal));
    }

    [Fact]
    public void EvidenceNote_OverLength_IsRejectedWithIndex()
    {
        var candidate = CompletedResult() with
        {
            Evidence =
            [
                new WorkerEvidence
                {
                    Path = "src/a.cs",
                    Note = new string('n', WorkerResultLimits.MaxEvidenceNoteLength + 1),
                },
            ],
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("evidence[0].note", StringComparison.Ordinal));
    }

    [Fact]
    public void Evidence_WithPathOverLength_IsRejectedWithLimit()
    {
        var candidate = CompletedResult() with
        {
            Evidence = [new WorkerEvidence { Path = new string('p', WorkerResultLimits.MaxEvidencePathLength + 1) }],
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("evidence[0].path", error, StringComparison.Ordinal);
        Assert.Contains(WorkerResultLimits.MaxEvidencePathLength.ToString(), error, StringComparison.Ordinal);
    }

    // ---- conclusion / statusDetail lengths ------------------------------------------

    [Fact]
    public void Conclusion_AtExactLimit_IsValid()
    {
        var candidate = CompletedResult() with { Conclusion = new string('a', WorkerResultLimits.MaxConclusionLength) };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Conclusion_OverLimit_IsRejectedWithLimit()
    {
        var candidate = CompletedResult() with { Conclusion = new string('a', WorkerResultLimits.MaxConclusionLength + 1) };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("'conclusion'", error, StringComparison.Ordinal);
        Assert.Contains(WorkerResultLimits.MaxConclusionLength.ToString(), error, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusDetail_AtExactLimit_IsValid()
    {
        var candidate = NonCompletedResult(WorkerResultStatus.Incomplete) with
        {
            StatusDetail = new string('d', WorkerResultLimits.MaxStatusDetailLength),
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void StatusDetail_OverLimit_IsRejectedWithLimit()
    {
        var candidate = NonCompletedResult(WorkerResultStatus.Incomplete) with
        {
            StatusDetail = new string('d', WorkerResultLimits.MaxStatusDetailLength + 1),
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("'statusDetail'", error, StringComparison.Ordinal);
        Assert.Contains(WorkerResultLimits.MaxStatusDetailLength.ToString(), error, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletedResult_WithLongWhitespaceStatusDetail_LengthLimitStillApplies()
    {
        // 空白 statusDetail 在形状规则中等同于“空”，因此只报长度错误；长度上限不受状态影响。
        // Whitespace statusDetail counts as empty for the shape rule, so only the length error fires;
        // the length limit applies regardless of status.
        var candidate = CompletedResult() with
        {
            StatusDetail = new string(' ', WorkerResultLimits.MaxStatusDetailLength + 1),
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("'statusDetail'", error, StringComparison.Ordinal);
        Assert.Contains("exceeds the maximum length", error, StringComparison.Ordinal);
    }

    [Fact]
    public void NonCompletedResult_WithLongWhitespaceStatusDetail_IsRejectedForShapeAndLength()
    {
        var candidate = NonCompletedResult(WorkerResultStatus.Incomplete) with
        {
            StatusDetail = new string(' ', WorkerResultLimits.MaxStatusDetailLength + 1),
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains(result.Errors, error => error.Contains("is required", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("exceeds the maximum length", StringComparison.Ordinal));
    }

    // ---- suggestion / uncertainty lists ------------------------------------------------

    [Fact]
    public void SingleList_AtExactPerListLimits_IsValid()
    {
        var candidate = CompletedResult() with
        {
            SuggestedChanges = Enumerable.Range(1, WorkerResultLimits.MaxListCount)
                                         .Select(i => new string('c', WorkerResultLimits.MaxListEntryLength))
                                         .ToArray(),
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Lists_OverTotalBudget_AreRejectedByTotalLimit()
    {
        var fullList = Enumerable.Range(1, WorkerResultLimits.MaxListCount)
                                 .Select(i => new string('x', WorkerResultLimits.MaxListEntryLength))
                                 .ToArray();
        var candidate = CompletedResult() with
        {
            SuggestedChanges = fullList,
            TestSuggestions  = fullList,
            Uncertainties    = fullList,
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("maximum total", error, StringComparison.Ordinal);
    }

    [Fact]
    public void List_OverCount_IsRejected()
    {
        var candidate = CompletedResult() with
        {
            SuggestedChanges = Enumerable.Range(1, WorkerResultLimits.MaxListCount + 1)
                                         .Select(i => $"change {i}")
                                         .ToArray(),
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("'suggestedChanges'", StringComparison.Ordinal));
    }

    [Fact]
    public void ListEntry_OverLength_IsRejectedWithIndex()
    {
        var candidate = CompletedResult() with
        {
            TestSuggestions = [new string('t', WorkerResultLimits.MaxListEntryLength + 1)],
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Contains("testSuggestions[0]", error, StringComparison.Ordinal);
        Assert.Contains(WorkerResultLimits.MaxListEntryLength.ToString(), error, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankListEntry_IsRejectedWithIndex()
    {
        var candidate = CompletedResult() with { Uncertainties = ["real uncertainty", "   "] };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("uncertainties[1]", StringComparison.Ordinal));
    }

    // ---- total budget ---------------------------------------------------------------------

    [Fact]
    public void TotalText_AtExactLimit_IsValid()
    {
        var candidate = CompletedResult() with
        {
            Conclusion       = new string('a', WorkerResultLimits.MaxConclusionLength),
            SuggestedChanges = Enumerable.Range(1, WorkerResultLimits.MaxListCount)
                                         .Select(i => new string('c', WorkerResultLimits.MaxListEntryLength))
                                         .ToArray(),
            TestSuggestions = Enumerable.Range(1, WorkerResultLimits.MaxListCount - 2)
                                        .Select(i => new string('t', WorkerResultLimits.MaxListEntryLength))
                                        .ToArray(),
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void TotalText_OverLimit_IsRejectedAlongsideEntryLimit()
    {
        var candidate = CompletedResult() with
        {
            Conclusion       = new string('a', WorkerResultLimits.MaxConclusionLength),
            SuggestedChanges = Enumerable.Range(1, WorkerResultLimits.MaxListCount)
                                         .Select(i => new string('c', WorkerResultLimits.MaxListEntryLength))
                                         .ToArray(),
            TestSuggestions = Enumerable.Range(1, WorkerResultLimits.MaxListCount - 2)
                                        .Select(i => new string('t', WorkerResultLimits.MaxListEntryLength))
                                        .Append(new string('t', WorkerResultLimits.MaxListEntryLength + 1))
                                        .ToArray(),
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("testSuggestions[6]", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("maximum total", StringComparison.Ordinal));
    }

    [Fact]
    public void OversizedEvidence_TotalBudgetErrorCarriesExactWideTotal()
    {
        const int entryCount = 30_000;
        var candidate = CompletedResult() with
        {
            Evidence = Enumerable.Repeat(new WorkerEvidence { Path = new string('p', WorkerResultLimits.MaxEvidencePathLength) },
                                         entryCount)
                                 .ToArray(),
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("'evidence'", StringComparison.Ordinal));
        var expectedTotal = (long)entryCount * WorkerResultLimits.MaxEvidencePathLength + candidate.Conclusion.Length;
        Assert.Contains(result.Errors, error => error.Contains($"(actual: {expectedTotal})", StringComparison.Ordinal));
    }

    // ---- accumulation and null handling ------------------------------------------------------

    [Fact]
    public void MultipleInvalidParts_AreAllReported()
    {
        var candidate = new WorkerResult
        {
            Status     = WorkerResultStatus.Completed,
            Conclusion = string.Empty,
            Evidence   = [new WorkerEvidence { Path = "C:\\outside\\file.cs" }],
            Statistics = new WorkerExecutionStats { ToolCalls = -2 },
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("'conclusion'", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("evidence[0].path", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("statistics.toolCalls", StringComparison.Ordinal));
    }

    [Fact]
    public void NullResult_IsInvalidWithoutThrowing()
    {
        var result = WorkerResultValidator.Validate(null);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void NullEvidenceEntry_IsRejectedWithIndex()
    {
        var candidate = CompletedResult() with { Evidence = [null!] };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("evidence[0]", StringComparison.Ordinal));
    }
}
