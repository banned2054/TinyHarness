using TinyHarness.Core.Context;

namespace TinyHarness.Tests;

public class StructuredStateTests
{
    [Fact]
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
            PendingWork        = ["rerun tests"],
        };

        var json = state.ToJson();

        Assert.True(StructuredState.TryParse(json, out var parsed));
        // StructuredState is a record of lists, so equality is structural field-by-field.
        Assert.Equal(state.Goal, parsed.Goal);
        Assert.Equal(state.Constraints, parsed.Constraints);
        Assert.Equal(state.Decisions, parsed.Decisions);
        Assert.Equal(state.FilesInspected, parsed.FilesInspected);
        Assert.Equal(state.FilesModified, parsed.FilesModified);
        Assert.Equal(state.CommandsAndResults, parsed.CommandsAndResults);
        Assert.Equal(state.PendingWork, parsed.PendingWork);
    }

    [Fact]
    public void TryParse_RejectsNonJson()
    {
        Assert.False(StructuredState.TryParse("not json", out _));
        Assert.False(StructuredState.TryParse("", out _));
        Assert.False(StructuredState.TryParse("   ", out _));
    }

    [Fact]
    public void TryParse_RejectsMistypedFields()
    {
        // "decisions" must be an array of strings, not a bare string.
        const string mistyped =
            """{"goal":"g","constraints":[],"decisions":"nope","filesInspected":[],"filesModified":[],"commandsAndResults":[],"pendingWork":[]}""";

        Assert.False(StructuredState.TryParse(mistyped, out _));
    }

    [Fact]
    public void TryParse_AcceptsMissingOptionalFieldsAsEmpty()
    {
        const string minimal = """{"goal":"g"}""";

        Assert.True(StructuredState.TryParse(minimal, out var state));
        Assert.Equal("g", state.Goal);
        Assert.Empty(state.Decisions);
        Assert.Empty(state.PendingWork);
    }

    [Fact]
    public void BlankEntries_AreNormalizedAndDoNotMakeTheStateNonEmpty()
    {
        const string blank =
            """{"goal":"  ","constraints":["  "],"decisions":[null],"filesInspected":[],"filesModified":[],"commandsAndResults":[],"pendingWork":[""]}""";

        Assert.True(StructuredState.TryParse(blank, out var state));
        Assert.True(state.IsEmpty);
        Assert.Empty(state.Constraints);
        Assert.Empty(state.Decisions);
        Assert.Empty(state.PendingWork);
        Assert.DoesNotContain("isEmpty", state.ToJson(), StringComparison.OrdinalIgnoreCase);
    }
}
