using TinyHarness.Core.Models.Context;

namespace TinyHarness.Tests;

public class StructuredStateTests
{
    [Test]
    public void ToJson_And_TryParse_RoundTripAllFields()
    {
        var state = new StructuredState
        {
            Goal               = "fix the build",
            Constraints        = ["stay in workspace"],
            Decisions          = ["run dotnet test"],
            FilesInspected     = ["src/A.cs"],
            FilesModified      = ["src/A.cs"],
            CommandsAndResults = ["dotnet test: 12 passed"],
            PendingWork        = ["rerun tests"]
        };

        var json = state.ToJson();

        Assert.That(StructuredState.TryParse(json, out var parsed), Is.True);
        // StructuredState 是列表组成的 record，相等性按字段逐项比较。
        Assert.That(parsed.Goal, Is.EqualTo(state.Goal));
        Assert.That(parsed.Constraints, Is.EqualTo(state.Constraints));
        Assert.That(parsed.Decisions, Is.EqualTo(state.Decisions));
        Assert.That(parsed.FilesInspected, Is.EqualTo(state.FilesInspected));
        Assert.That(parsed.FilesModified, Is.EqualTo(state.FilesModified));
        Assert.That(parsed.CommandsAndResults, Is.EqualTo(state.CommandsAndResults));
        Assert.That(parsed.PendingWork, Is.EqualTo(state.PendingWork));
    }

    [Test]
    public void TryParse_RejectsNonJson()
    {
        Assert.That(StructuredState.TryParse("not json", out _), Is.False);
        Assert.That(StructuredState.TryParse("", out _), Is.False);
        Assert.That(StructuredState.TryParse("   ", out _), Is.False);
    }

    [Test]
    public void TryParse_RejectsMistypedFields()
    {
        // "decisions" 必须是字符串数组，不能是裸字符串。
        const string mistyped =
            """{"goal":"g","constraints":[],"decisions":"nope","filesInspected":[],"filesModified":[],"commandsAndResults":[],"pendingWork":[]}""";

        Assert.That(StructuredState.TryParse(mistyped, out _), Is.False);
    }

    [Test]
    public void TryParse_AcceptsMissingOptionalFieldsAsEmpty()
    {
        const string minimal = """{"goal":"g"}""";

        Assert.That(StructuredState.TryParse(minimal, out var state), Is.True);
        Assert.That(state.Goal, Is.EqualTo("g"));
        Assert.That(state.Decisions, Is.Empty);
        Assert.That(state.PendingWork, Is.Empty);
    }

    [Test]
    public void BlankEntries_AreNormalizedAndDoNotMakeTheStateNonEmpty()
    {
        const string blank =
            """{"goal":"  ","constraints":["  "],"decisions":[null],"filesInspected":[],"filesModified":[],"commandsAndResults":[],"pendingWork":[""]}""";

        Assert.That(StructuredState.TryParse(blank, out var state), Is.True);
        Assert.That(state.IsEmpty, Is.True);
        Assert.That(state.Constraints, Is.Empty);
        Assert.That(state.Decisions, Is.Empty);
        Assert.That(state.PendingWork, Is.Empty);
        TestAssert.DoesNotContain("isEmpty", state.ToJson(), StringComparison.OrdinalIgnoreCase);
    }
}
