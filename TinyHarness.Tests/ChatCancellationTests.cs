using TinyHarness.Cli.Services;

namespace TinyHarness.Tests;

/// <summary>
///     chat 的 Ctrl+C 协调测试：空闲取消结束会话，活动回合取消只取消该回合（会话保持可用），
///     回合令牌链接外部令牌，EndTurn 后可开始下一个回合（PLAN §6/§10 M12.3）。
///     Chat Ctrl+C coordination tests: an idle cancel ends the session, an active-turn cancel
///     cancels only that turn (the session stays usable), the turn token links the external
///     token, and EndTurn allows the next turn to begin (PLAN §6/§10 M12.3).
/// </summary>
public class ChatCancellationTests
{
    [Fact]
    public void RaiseCancel_WhileIdle_CancelsTheSession()
    {
        using var cancellation = new ChatCancellation(registerConsole : false);

        Assert.False(cancellation.IsSessionCancelled);

        cancellation.RaiseCancel();

        Assert.True(cancellation.IsSessionCancelled);
        Assert.True(cancellation.SessionToken.IsCancellationRequested);
    }

    [Fact]
    public void RaiseCancel_DuringActiveTurn_CancelsOnlyTheTurn()
    {
        using var cancellation = new ChatCancellation(registerConsole : false);
        var turnToken = cancellation.BeginTurn(CancellationToken.None);

        cancellation.RaiseCancel();

        Assert.True(turnToken.IsCancellationRequested);
        Assert.False(cancellation.IsSessionCancelled);

        cancellation.EndTurn();
        cancellation.RaiseCancel();

        Assert.True(cancellation.IsSessionCancelled);
    }

    [Fact]
    public void BeginTurn_LinksTheExternalToken()
    {
        using var cancellation = new ChatCancellation(registerConsole : false);
        using var external    = new CancellationTokenSource();
        var turnToken = cancellation.BeginTurn(external.Token);

        external.Cancel();

        Assert.True(turnToken.IsCancellationRequested);
        Assert.False(cancellation.IsSessionCancelled);
        cancellation.EndTurn();
    }

    [Fact]
    public void EndTurn_AllowsTheNextTurnToBegin()
    {
        using var cancellation = new ChatCancellation(registerConsole : false);

        var first = cancellation.BeginTurn(CancellationToken.None);
        cancellation.RaiseCancel();
        Assert.True(first.IsCancellationRequested);
        cancellation.EndTurn();

        var second = cancellation.BeginTurn(CancellationToken.None);
        Assert.False(second.IsCancellationRequested);
        Assert.False(cancellation.IsSessionCancelled);
        cancellation.EndTurn();
    }
}
