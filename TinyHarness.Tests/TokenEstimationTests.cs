using System.Text.Json.Nodes;
using TinyHarness.Core.ChatCompletions;
using TinyHarness.Core.Context;
using TinyHarness.Core.Tools;

namespace TinyHarness.Tests;

public class TokenEstimationTests
{
    [Fact]
    public void EstimateText_IsDeterministicAndConservative()
    {
        Assert.Equal(TokenEstimator.EstimateText("alpha beta gamma"), TokenEstimator.EstimateText("alpha beta gamma"));

        // ASCII is estimated at (chars + 3) / 4, so the estimate never undercuts
        // a plain length/4 bound and stays deterministic.
        for (var length = 0; length <= 64; length++)
        {
            var text = new string('a', length);
            Assert.Equal(TokenEstimator.EstimateText(text), TokenEstimator.EstimateText(text));
            Assert.True(TokenEstimator.EstimateText(text) >= (length + 3) / 4,
                        $"estimate {TokenEstimator.EstimateText(text)} < ceil({length}/4)");
        }

        Assert.Equal(0, TokenEstimator.EstimateText(string.Empty));
        Assert.Equal(0, TokenEstimator.EstimateText(null!));
    }

    [Fact]
    public void EstimateText_CountsWideCharactersAsOneTokenEach()
    {
        // 4 CJK chars would be 1 token at 4 chars/token but are conservatively 4.
        Assert.Equal(4, TokenEstimator.EstimateText("中文测试"));
        // Two CJK chars cost more than two ASCII chars (2 vs ceil(2/4)=1).
        Assert.True(TokenEstimator.EstimateText("中文") > TokenEstimator.EstimateText("ab"));
    }

    [Fact]
    public void EstimateMessage_AddsFixedOverheadAndScalesWithContent()
    {
        var baseMessage   = ChatMessage.User("abc");
        var longerMessage = ChatMessage.User("abcdefgh");

        Assert.True(TokenEstimator.EstimateMessage(longerMessage) > TokenEstimator.EstimateMessage(baseMessage));

        // Assistant tool-call rounds cost more than plain assistant text.
        var plain = ChatMessage.Assistant("text");
        var withCall = ChatMessage.Assistant(string.Empty,
                                             [new ChatToolCall("c1", "tool", "{\"x\":1}")]);
        Assert.True(TokenEstimator.EstimateMessage(withCall) > TokenEstimator.EstimateMessage(plain));
    }

    [Fact]
    public void EstimateToolDefinitions_AccountsForSchemaText()
    {
        var tools = new List<ToolDefinition>
        {
            new()
            {
                Name = "alpha", Description = "describes alpha", Parameters = new JsonObject()
            },
        };

        var estimate = TokenEstimator.EstimateToolDefinitions(tools);

        Assert.True(estimate > TokenEstimator.EstimateText("describes alpha"),
                    "the schema JSON and per-tool overhead must count toward the estimate");
    }
}
