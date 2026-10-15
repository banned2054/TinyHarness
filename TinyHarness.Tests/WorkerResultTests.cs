using TinyHarness.Core.Models.Worker;

namespace TinyHarness.Tests;

public class WorkerResultTests
{
    [Test]
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
                    Note      = "The budget check happens here."
                }
            ],
            Uncertainties = ["Whether the limit applies before or after the first attempt."],
            Statistics = new WorkerExecutionStats
            {
                ModelRequests        = 3,
                ToolCalls            = 12,
                ToolOutputCharacters = 48_000,
                Elapsed              = TimeSpan.FromSeconds(90)
            }
        };

        Assert.That(result.Status, Is.Not.EqualTo(WorkerResultStatus.Completed));
        Assert.That(result.Conclusion, Is.EqualTo(string.Empty));
        Assert.That(string.IsNullOrEmpty(result.StatusDetail), Is.False);
        var evidence = TestAssert.Single(result.Evidence);
        Assert.That(evidence.Path, Is.EqualTo("src/retry.cs"));
        Assert.That(evidence.LineStart, Is.EqualTo(42));
        Assert.That(evidence.LineEnd, Is.EqualTo(58));
        Assert.That(result.Statistics.ToolCalls, Is.EqualTo(12));
    }

    [Test]
    public void CompletedResult_CanCarryConclusionWithSuggestions()
    {
        var result = new WorkerResult
        {
            Status           = WorkerResultStatus.Completed,
            Conclusion       = "The retry budget is enforced in RetryPolicy.Evaluate.",
            Evidence         = [new WorkerEvidence { Path = "src/RetryPolicy.cs", LineStart = 17 }],
            SuggestedChanges = ["Move the budget check before the first attempt."],
            TestSuggestions  = ["Add a unit test that exhausts the budget on the first attempt."],
            Uncertainties    = ["Behavior on concurrent calls is unverified."]
        };

        Assert.That(result.Status, Is.EqualTo(WorkerResultStatus.Completed));
        Assert.That(string.IsNullOrEmpty(result.Conclusion), Is.False);
        TestAssert.Single(result.SuggestedChanges);
        TestAssert.Single(result.TestSuggestions);
        TestAssert.Single(result.Uncertainties);
        Assert.That(result.Statistics.ModelRequests, Is.EqualTo(0));
    }
}
