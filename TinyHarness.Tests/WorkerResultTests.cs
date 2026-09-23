using TinyHarness.Core.Models.Worker;

namespace TinyHarness.Tests;

public class WorkerResultTests
{
    [Fact]
    public void IncompleteResult_CanCarryEvidenceAndStatsWithoutConclusion()
    {
        var result = new WorkerResult
        {
            Status       = WorkerResultStatus.Incomplete,
            StatusDetail = "Stopped at the tool-call limit; no final conclusion was reached.",
            Evidence =
            [
                new WorkerEvidence
                {
                    Path      = "src/retry.cs",
                    LineStart = 42,
                    LineEnd   = 58,
                    Note      = "The budget check happens here.",
                },
            ],
            Uncertainties = ["Whether the limit applies before or after the first attempt."],
            Statistics    = new WorkerExecutionStats
            {
                ModelRequests         = 3,
                ToolCalls             = 12,
                ToolOutputCharacters  = 48_000,
                Elapsed               = TimeSpan.FromSeconds(90),
            },
        };

        Assert.NotEqual(WorkerResultStatus.Completed, result.Status);
        Assert.Equal(string.Empty, result.Conclusion);
        Assert.False(string.IsNullOrEmpty(result.StatusDetail));
        var evidence = Assert.Single(result.Evidence);
        Assert.Equal("src/retry.cs", evidence.Path);
        Assert.Equal(42, evidence.LineStart);
        Assert.Equal(58, evidence.LineEnd);
        Assert.Equal(12, result.Statistics.ToolCalls);
    }

    [Fact]
    public void CompletedResult_CanCarryConclusionWithSuggestions()
    {
        var result = new WorkerResult
        {
            Status            = WorkerResultStatus.Completed,
            Conclusion        = "The retry budget is enforced in RetryPolicy.Evaluate.",
            Evidence          = [new WorkerEvidence { Path = "src/RetryPolicy.cs", LineStart = 17 }],
            SuggestedChanges  = ["Move the budget check before the first attempt."],
            TestSuggestions   = ["Add a unit test that exhausts the budget on the first attempt."],
            Uncertainties     = ["Behavior on concurrent calls is unverified."],
        };

        Assert.Equal(WorkerResultStatus.Completed, result.Status);
        Assert.False(string.IsNullOrEmpty(result.Conclusion));
        Assert.Single(result.SuggestedChanges);
        Assert.Single(result.TestSuggestions);
        Assert.Single(result.Uncertainties);
        Assert.Equal(0, result.Statistics.ModelRequests);
    }
}
