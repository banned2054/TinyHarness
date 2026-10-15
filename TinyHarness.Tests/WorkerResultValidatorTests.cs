using TinyHarness.Core.Models.Worker;
using TinyHarness.Core.Services.Worker;

namespace TinyHarness.Tests;

public class WorkerResultValidatorTests
{
    private static WorkerResult CompletedResult() => new()
    {
        Status     = WorkerResultStatus.Completed,
        Conclusion = "The retry budget is enforced in RetryPolicy.Evaluate."
    };

    private static WorkerResult NonCompletedResult(WorkerResultStatus status) => new()
    {
        Status       = status,
        StatusDetail = "Stopped at the tool-call limit; no final conclusion was reached."
    };

    // ---- legal shapes per status ---------------------------------------------

    [Test]
    public void CompletedResult_WithConclusionOnly_IsValid()
    {
        var result = WorkerResultValidator.Validate(CompletedResult());

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }

    [Test]
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
                    Note      = "The budget check happens here."
                }
            ],
            SuggestedChanges = ["Move the budget check before the first attempt."],
            TestSuggestions = ["Add a unit test that exhausts the budget on the first attempt."],
            Uncertainties = ["Behavior on concurrent calls is unverified."],
            Statistics = new WorkerExecutionStats
            {
                ModelRequests        = 3,
                ToolCalls            = 12,
                ToolOutputCharacters = 48_000,
                Elapsed              = TimeSpan.FromSeconds(90)
            }
        };

        var result = WorkerResultValidator.Validate(request);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }


    [TestCase(WorkerResultStatus.Incomplete)]
    [TestCase(WorkerResultStatus.Failed)]
    [TestCase(WorkerResultStatus.Cancelled)]
    public void NonCompletedResult_WithStatusDetailOnly_IsValid(WorkerResultStatus status)
    {
        var result = WorkerResultValidator.Validate(NonCompletedResult(status));

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }

    // ---- illegal status shapes -------------------------------------------------

    [Test]
    public void CompletedResult_WithoutConclusion_IsRejected()
    {
        var candidate = CompletedResult() with { Conclusion = string.Empty };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("'conclusion'", StringComparison.Ordinal), result.Errors);
    }

    [Test]
    public void CompletedResult_WithStatusDetail_IsRejected()
    {
        var candidate = CompletedResult() with { StatusDetail = "leftover note" };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("'statusDetail'", StringComparison.Ordinal), result.Errors);
    }


    [TestCase(WorkerResultStatus.Incomplete)]
    [TestCase(WorkerResultStatus.Failed)]
    [TestCase(WorkerResultStatus.Cancelled)]
    public void NonCompletedResult_WithConclusion_IsRejected(WorkerResultStatus status)
    {
        var candidate = NonCompletedResult(status) with { Conclusion = "Looks done to me." };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("'conclusion'", StringComparison.Ordinal), result.Errors);
    }


    [TestCase(WorkerResultStatus.Incomplete)]
    [TestCase(WorkerResultStatus.Failed)]
    [TestCase(WorkerResultStatus.Cancelled)]
    public void NonCompletedResult_WithoutStatusDetail_IsRejected(WorkerResultStatus status)
    {
        var candidate = NonCompletedResult(status) with { StatusDetail = "  " };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("'statusDetail'", StringComparison.Ordinal), result.Errors);
    }

    [Test]
    public void UnknownStatusValue_IsRejected()
    {
        var candidate = CompletedResult() with { Status = (WorkerResultStatus)99 };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("'status'", StringComparison.Ordinal), result.Errors);
    }

    // ---- statistics --------------------------------------------------------------

    [Test]
    public void NegativeModelRequests_IsRejected()
    {
        var candidate = CompletedResult() with { Statistics = new WorkerExecutionStats { ModelRequests = -1 } };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("statistics.modelRequests", StringComparison.Ordinal),
                               result.Errors);
    }

    [Test]
    public void NegativeToolCalls_IsRejected()
    {
        var candidate = CompletedResult() with { Statistics = new WorkerExecutionStats { ToolCalls = -1 } };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("statistics.toolCalls", StringComparison.Ordinal),
                               result.Errors);
    }

    [Test]
    public void NegativeToolOutputCharacters_IsRejected()
    {
        var candidate = CompletedResult() with
        {
            Statistics = new WorkerExecutionStats { ToolOutputCharacters = -1 }
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("statistics.toolOutputCharacters", StringComparison.Ordinal),
                               result.Errors);
    }

    [Test]
    public void NegativeElapsed_IsRejected()
    {
        var candidate = CompletedResult() with
        {
            Statistics = new WorkerExecutionStats { Elapsed = TimeSpan.FromSeconds(-1) }
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("statistics.elapsed", StringComparison.Ordinal), result.Errors);
    }

    [Test]
    public void NullStatistics_IsRejectedWithoutThrowing()
    {
        var candidate = CompletedResult() with { Statistics = null! };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("'statistics'", StringComparison.Ordinal), result.Errors);
    }

    // ---- evidence ------------------------------------------------------------------

    [Test]
    public void Evidence_AtExactCountLimit_IsValid()
    {
        var candidate = CompletedResult() with
        {
            Evidence = Enumerable.Range(1, WorkerResultLimits.MaxEvidenceCount)
                                 .Select(i => new WorkerEvidence
                                  {
                                      Path = new string('p', WorkerResultLimits.MaxEvidencePathLength - 4) +
                                             $"/p{i:D2}",
                                      LineStart = 1,
                                      Note      = new string('n', WorkerResultLimits.MaxEvidenceNoteLength)
                                  })
                                 .ToArray()
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }

    [Test]
    public void Evidence_OverCount_IsRejected()
    {
        var candidate = CompletedResult() with
        {
            Evidence = Enumerable.Range(1, WorkerResultLimits.MaxEvidenceCount + 1)
                                 .Select(i => new WorkerEvidence { Path = $"src/file{i}.cs" })
                                 .ToArray()
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("'evidence'", StringComparison.Ordinal), result.Errors);
    }


    [TestCase("")]
    [TestCase("   ")]
    [TestCase("C:\\src\\file.cs")]
    [TestCase("C:/src/file.cs")]
    [TestCase("C:relative.txt")]
    [TestCase("c:\\lower\\drive.cs")]
    [TestCase("/absolute/file.cs")]
    [TestCase("\\rooted.txt")]
    [TestCase("\\\\server\\share\\file.cs")]
    [TestCase("//host/share/file.cs")]
    [TestCase("../outside.txt")]
    [TestCase("src/../../outside.txt")]
    [TestCase("bad\u0001path.cs")]
    public void EvidencePath_InvalidShape_IsRejectedWithIndex(string path)
    {
        var candidate = CompletedResult() with { Evidence = [new WorkerEvidence { Path = path }] };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("evidence[0].path", StringComparison.Ordinal), result.Errors);
    }

    [Test]
    public void EvidenceLineNumbers_ValidCombinations_AreAccepted()
    {
        var candidate = CompletedResult() with
        {
            Evidence =
            [
                new WorkerEvidence { Path = "src/a.cs" },
                new WorkerEvidence { Path = "src/b.cs", LineStart = 42 },
                new WorkerEvidence { Path = "src/c.cs", LineStart = 42, LineEnd = 42 },
                new WorkerEvidence { Path = "src/d.cs", LineStart = 1, LineEnd  = 10 }
            ]
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }


    [TestCase(0)]
    [TestCase(-3)]
    public void EvidenceLineStart_BelowOne_IsRejected(int lineStart)
    {
        var candidate = CompletedResult() with
        {
            Evidence = [new WorkerEvidence { Path = "src/a.cs", LineStart = lineStart }]
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("evidence[0].lineStart", StringComparison.Ordinal),
                               result.Errors);
    }

    [Test]
    public void EvidenceLineEnd_SmallerThanLineStart_IsRejected()
    {
        var candidate = CompletedResult() with
        {
            Evidence = [new WorkerEvidence { Path = "src/a.cs", LineStart = 10, LineEnd = 5 }]
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("evidence[0].lineEnd", StringComparison.Ordinal), result.Errors);
    }

    [Test]
    public void EvidenceLineEnd_WithoutLineStart_IsRejected()
    {
        var candidate = CompletedResult() with
        {
            Evidence = [new WorkerEvidence { Path = "src/a.cs", LineEnd = 5 }]
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("evidence[0].lineEnd", StringComparison.Ordinal), result.Errors);
    }

    [Test]
    public void EvidenceNote_OverLength_IsRejectedWithIndex()
    {
        var candidate = CompletedResult() with
        {
            Evidence =
            [
                new WorkerEvidence
                {
                    Path = "src/a.cs",
                    Note = new string('n', WorkerResultLimits.MaxEvidenceNoteLength + 1)
                }
            ]
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("evidence[0].note", StringComparison.Ordinal), result.Errors);
    }

    [Test]
    public void Evidence_WithPathOverLength_IsRejectedWithLimit()
    {
        var candidate = CompletedResult() with
        {
            Evidence = [new WorkerEvidence { Path = new string('p', WorkerResultLimits.MaxEvidencePathLength + 1) }]
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        var error = TestAssert.Single(result.Errors);
        TestAssert.Contains("evidence[0].path", error, StringComparison.Ordinal);
        TestAssert.Contains(WorkerResultLimits.MaxEvidencePathLength.ToString(), error, StringComparison.Ordinal);
    }

    // ---- conclusion / statusDetail lengths ------------------------------------------

    [Test]
    public void Conclusion_AtExactLimit_IsValid()
    {
        var candidate = CompletedResult() with { Conclusion = new string('a', WorkerResultLimits.MaxConclusionLength) };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.True);
    }

    [Test]
    public void Conclusion_OverLimit_IsRejectedWithLimit()
    {
        var candidate = CompletedResult() with
        {
            Conclusion = new string('a', WorkerResultLimits.MaxConclusionLength + 1)
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        var error = TestAssert.Single(result.Errors);
        TestAssert.Contains("'conclusion'", error, StringComparison.Ordinal);
        TestAssert.Contains(WorkerResultLimits.MaxConclusionLength.ToString(), error, StringComparison.Ordinal);
    }

    [Test]
    public void StatusDetail_AtExactLimit_IsValid()
    {
        var candidate = NonCompletedResult(WorkerResultStatus.Incomplete) with
        {
            StatusDetail = new string('d', WorkerResultLimits.MaxStatusDetailLength)
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.True);
    }

    [Test]
    public void StatusDetail_OverLimit_IsRejectedWithLimit()
    {
        var candidate = NonCompletedResult(WorkerResultStatus.Incomplete) with
        {
            StatusDetail = new string('d', WorkerResultLimits.MaxStatusDetailLength + 1)
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        var error = TestAssert.Single(result.Errors);
        TestAssert.Contains("'statusDetail'", error, StringComparison.Ordinal);
        TestAssert.Contains(WorkerResultLimits.MaxStatusDetailLength.ToString(), error, StringComparison.Ordinal);
    }

    [Test]
    public void CompletedResult_WithLongWhitespaceStatusDetail_LengthLimitStillApplies()
    {
        // 空白 statusDetail 在形状规则中等同于“空”，因此只报长度错误；长度上限不受状态影响。
        // 空白 statusDetail 按形状规则视为空，因此只触发长度错误；长度上限与状态无关。
        var candidate = CompletedResult() with
        {
            StatusDetail = new string(' ', WorkerResultLimits.MaxStatusDetailLength + 1)
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        var error = TestAssert.Single(result.Errors);
        TestAssert.Contains("'statusDetail'", error, StringComparison.Ordinal);
        TestAssert.Contains("exceeds the maximum length", error, StringComparison.Ordinal);
    }

    [Test]
    public void NonCompletedResult_WithLongWhitespaceStatusDetail_IsRejectedForShapeAndLength()
    {
        var candidate = NonCompletedResult(WorkerResultStatus.Incomplete) with
        {
            StatusDetail = new string(' ', WorkerResultLimits.MaxStatusDetailLength + 1)
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors.Count, Is.EqualTo(2));
        TestAssert.ContainsAll(error => error.Contains("is required", StringComparison.Ordinal), result.Errors);
        TestAssert.ContainsAll(error => error.Contains("exceeds the maximum length", StringComparison.Ordinal),
                               result.Errors);
    }

    // ---- suggestion / uncertainty lists ------------------------------------------------

    [Test]
    public void SingleList_AtExactPerListLimits_IsValid()
    {
        var candidate = CompletedResult() with
        {
            SuggestedChanges = Enumerable.Range(1, WorkerResultLimits.MaxListCount)
                                         .Select(i => new string('c', WorkerResultLimits.MaxListEntryLength))
                                         .ToArray()
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }

    [Test]
    public void Lists_OverTotalBudget_AreRejectedByTotalLimit()
    {
        var fullList = Enumerable.Range(1, WorkerResultLimits.MaxListCount)
                                 .Select(i => new string('x', WorkerResultLimits.MaxListEntryLength))
                                 .ToArray();
        var candidate = CompletedResult() with
        {
            SuggestedChanges = fullList,
            TestSuggestions = fullList,
            Uncertainties = fullList
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        var error = TestAssert.Single(result.Errors);
        TestAssert.Contains("maximum total", error, StringComparison.Ordinal);
    }

    [Test]
    public void List_OverCount_IsRejected()
    {
        var candidate = CompletedResult() with
        {
            SuggestedChanges = Enumerable.Range(1, WorkerResultLimits.MaxListCount + 1)
                                         .Select(i => $"change {i}")
                                         .ToArray()
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("'suggestedChanges'", StringComparison.Ordinal), result.Errors);
    }

    [Test]
    public void ListEntry_OverLength_IsRejectedWithIndex()
    {
        var candidate = CompletedResult() with
        {
            TestSuggestions = [new string('t', WorkerResultLimits.MaxListEntryLength + 1)]
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        var error = TestAssert.Single(result.Errors);
        TestAssert.Contains("testSuggestions[0]", error, StringComparison.Ordinal);
        TestAssert.Contains(WorkerResultLimits.MaxListEntryLength.ToString(), error, StringComparison.Ordinal);
    }

    [Test]
    public void BlankListEntry_IsRejectedWithIndex()
    {
        var candidate = CompletedResult() with { Uncertainties = ["real uncertainty", "   "] };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("uncertainties[1]", StringComparison.Ordinal), result.Errors);
    }

    // ---- total budget ---------------------------------------------------------------------

    [Test]
    public void TotalText_AtExactLimit_IsValid()
    {
        var candidate = CompletedResult() with
        {
            Conclusion = new string('a', WorkerResultLimits.MaxConclusionLength),
            SuggestedChanges = Enumerable.Range(1, WorkerResultLimits.MaxListCount)
                                         .Select(i => new string('c', WorkerResultLimits.MaxListEntryLength))
                                         .ToArray(),
            TestSuggestions = Enumerable.Range(1, WorkerResultLimits.MaxListCount - 2)
                                        .Select(i => new string('t', WorkerResultLimits.MaxListEntryLength))
                                        .ToArray()
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }

    [Test]
    public void TotalText_OverLimit_IsRejectedAlongsideEntryLimit()
    {
        var candidate = CompletedResult() with
        {
            Conclusion = new string('a', WorkerResultLimits.MaxConclusionLength),
            SuggestedChanges = Enumerable.Range(1, WorkerResultLimits.MaxListCount)
                                         .Select(i => new string('c', WorkerResultLimits.MaxListEntryLength))
                                         .ToArray(),
            TestSuggestions = Enumerable.Range(1, WorkerResultLimits.MaxListCount - 2)
                                        .Select(i => new string('t', WorkerResultLimits.MaxListEntryLength))
                                        .Append(new string('t', WorkerResultLimits.MaxListEntryLength + 1))
                                        .ToArray()
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("testSuggestions[6]", StringComparison.Ordinal), result.Errors);
        TestAssert.ContainsAll(error => error.Contains("maximum total", StringComparison.Ordinal), result.Errors);
    }

    [Test]
    public void OversizedEvidence_TotalBudgetErrorCarriesExactWideTotal()
    {
        const int entryCount = 30_000;
        var candidate = CompletedResult() with
        {
            Evidence = Enumerable.Repeat(new WorkerEvidence
                                             { Path = new string('p', WorkerResultLimits.MaxEvidencePathLength) },
                                         entryCount)
                                 .ToArray()
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("'evidence'", StringComparison.Ordinal), result.Errors);
        var expectedTotal = (long)entryCount * WorkerResultLimits.MaxEvidencePathLength + candidate.Conclusion.Length;
        TestAssert.ContainsAll(error => error.Contains($"(actual: {expectedTotal})", StringComparison.Ordinal),
                               result.Errors);
    }

    // ---- accumulation and null handling ------------------------------------------------------

    [Test]
    public void MultipleInvalidParts_AreAllReported()
    {
        var candidate = new WorkerResult
        {
            Status     = WorkerResultStatus.Completed,
            Conclusion = string.Empty,
            Evidence   = [new WorkerEvidence { Path = "C:\\outside\\file.cs" }],
            Statistics = new WorkerExecutionStats { ToolCalls = -2 }
        };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("'conclusion'", StringComparison.Ordinal), result.Errors);
        TestAssert.ContainsAll(error => error.Contains("evidence[0].path", StringComparison.Ordinal), result.Errors);
        TestAssert.ContainsAll(error => error.Contains("statistics.toolCalls", StringComparison.Ordinal),
                               result.Errors);
    }

    [Test]
    public void NullResult_IsInvalidWithoutThrowing()
    {
        var result = WorkerResultValidator.Validate(null);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Is.Not.Empty);
    }

    [Test]
    public void NullEvidenceEntry_IsRejectedWithIndex()
    {
        var candidate = CompletedResult() with { Evidence = [null!] };

        var result = WorkerResultValidator.Validate(candidate);

        Assert.That(result.IsValid, Is.False);
        TestAssert.ContainsAll(error => error.Contains("evidence[0]", StringComparison.Ordinal), result.Errors);
    }
}
