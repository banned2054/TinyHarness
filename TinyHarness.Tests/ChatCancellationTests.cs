using TinyHarness.Cli.Services;

namespace TinyHarness.Tests;

/// <summary>
///     <para>
///         chat 的 Ctrl+C 协调测试：空闲取消结束会话，活动回合取消只取消该回合（会话保持可用），
///         回合令牌链接外部令牌，EndTurn 后可开始下一个回合（PLAN §6/§10）。
///     </para>
///     <para>
///         Chat Ctrl+C coordination tests: an idle cancel ends the session, an active-turn cancel
///         cancels only that turn (the session stays usable), the turn token links the external
///         token, and EndTurn allows the next turn to begin (PLAN §6/§10).
///     </para>
/// </summary>
public class ChatCancellationTests
{
    [Test]
    public void RaiseCancel_WhileIdle_CancelsTheSession()
    {
        using var cancellation = new ChatCancellation(false);

        Assert.That(cancellation.IsSessionCancelled, Is.False);

        cancellation.RaiseCancel();

        Assert.That(cancellation.IsSessionCancelled, Is.True);
        Assert.That(cancellation.SessionToken.IsCancellationRequested, Is.True);
    }

    [Test]
    public void RaiseCancel_DuringActiveTurn_CancelsOnlyTheTurn()
    {
        using var cancellation = new ChatCancellation(false);
        var       turnToken    = cancellation.BeginTurn(CancellationToken.None);

        cancellation.RaiseCancel();

        Assert.That(turnToken.IsCancellationRequested, Is.True);
        Assert.That(cancellation.IsSessionCancelled, Is.False);

        cancellation.EndTurn();
        cancellation.RaiseCancel();

        Assert.That(cancellation.IsSessionCancelled, Is.True);
    }

    [Test]
    public void BeginTurn_LinksTheExternalToken()
    {
        using var cancellation = new ChatCancellation(false);
        using var external     = new CancellationTokenSource();
        var       turnToken    = cancellation.BeginTurn(external.Token);

        external.Cancel();

        Assert.That(turnToken.IsCancellationRequested, Is.True);
        Assert.That(cancellation.IsSessionCancelled, Is.False);
        cancellation.EndTurn();
    }

    [Test]
    public void EndTurn_AllowsTheNextTurnToBegin()
    {
        using var cancellation = new ChatCancellation(false);

        var first = cancellation.BeginTurn(CancellationToken.None);
        cancellation.RaiseCancel();
        Assert.That(first.IsCancellationRequested, Is.True);
        cancellation.EndTurn();

        var second = cancellation.BeginTurn(CancellationToken.None);
        Assert.That(second.IsCancellationRequested, Is.False);
        Assert.That(cancellation.IsSessionCancelled, Is.False);
        cancellation.EndTurn();
    }
}
