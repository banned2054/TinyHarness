using System.Text.Json.Nodes;
using TinyHarness.Core.Models.ChatCompletions;
using TinyHarness.Core.Models.Tools;
using TinyHarness.Core.Services.Context;

namespace TinyHarness.Tests;

public class TokenEstimationTests
{
    [Test]
    public void EstimateText_IsDeterministicAndConservative()
    {
        Assert.That(TokenEstimator.EstimateText("alpha beta gamma"),
                    Is.EqualTo(TokenEstimator.EstimateText("alpha beta gamma")));

        // ASCII 按（字符数 + 3）/ 4 估算：估算不低于朴素的 长度/4 下界且保持确定性。
        for (var length = 0; length <= 64; length++)
        {
            var text = new string('a', length);
            Assert.That(TokenEstimator.EstimateText(text), Is.EqualTo(TokenEstimator.EstimateText(text)));
            Assert.That(TokenEstimator.EstimateText(text) >= (length + 3) / 4, Is.True,
                        $"estimate {TokenEstimator.EstimateText(text)} < ceil({length}/4)");
        }

        Assert.That(TokenEstimator.EstimateText(string.Empty), Is.EqualTo(0));
        Assert.That(TokenEstimator.EstimateText(null!), Is.EqualTo(0));
    }

    [Test]
    public void EstimateText_CountsWideCharactersAsOneTokenEach()
    {
        // 4 个 CJK 字符按 4 字符/token 本应折 1 token，保守计为 4。
        Assert.That(TokenEstimator.EstimateText("中文测试"), Is.EqualTo(4));
        // 两个 CJK 字符成本高于两个 ASCII 字符（2 对 ceil(2/4)=1）。
        Assert.That(TokenEstimator.EstimateText("中文") > TokenEstimator.EstimateText("ab"), Is.True);
    }

    [Test]
    public void EstimateMessage_AddsFixedOverheadAndScalesWithContent()
    {
        var baseMessage   = ChatMessage.User("abc");
        var longerMessage = ChatMessage.User("abcdefgh");

        Assert.That(TokenEstimator.EstimateMessage(longerMessage) > TokenEstimator.EstimateMessage(baseMessage),
                    Is.True);

        // assistant 工具调用回合成本高于纯文本 assistant 消息。
        var plain = ChatMessage.Assistant("text");
        var withCall = ChatMessage.Assistant(string.Empty,
                                             [new ChatToolCall("c1", "tool", "{\"x\":1}")]);
        Assert.That(TokenEstimator.EstimateMessage(withCall) > TokenEstimator.EstimateMessage(plain), Is.True);
    }

    [Test]
    public void EstimateToolDefinitions_AccountsForSchemaText()
    {
        var tools = new List<ToolDefinition>
        {
            new()
            {
                Name = "alpha", Description = "describes alpha", Parameters = new JsonObject()
            }
        };

        var estimate = TokenEstimator.EstimateToolDefinitions(tools);

        Assert.That(estimate > TokenEstimator.EstimateText("describes alpha"), Is.True,
                    "the schema JSON and per-tool overhead must count toward the estimate");
    }
}
