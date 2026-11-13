using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using Eteris.Browser.Sync;
using Eteris.Sync.Google;

namespace Eteris.Browser.Tests.Sync;

// O estado tipado do início de sessão (0.2.15, T4): o que a barra da janela mostra vem daqui. Puro: tempo injetado,
// sem disco, sem rede, sem janelas.
public class SignInTrackerTests
{
    private long _now = 1_762_560_000_000;

    private long Now() => _now;

    private (SignInTracker Tracker, Func<int> Raised) NewTracker()
    {
        var tracker = new SignInTracker(Now);
        var raised = 0;
        tracker.Changed += () => raised++;
        return (tracker, () => raised);
    }

    [Fact]
    public void Tracker_Begin_IsWaiting_AndRaisesChangedOnce()
    {
        var (tracker, raised) = NewTracker();
        Assert.Equal(SignInStage.Idle, tracker.Current.Stage);
        Assert.False(tracker.Current.Ended);
        tracker.Begin(null);
        Assert.Equal(SignInStage.Waiting, tracker.Current.Stage);
        Assert.Equal(_now, tracker.Current.AtMs);
        Assert.Null(tracker.Current.ManualUrl);
        Assert.False(tracker.Current.Ended);
        Assert.Equal(1, raised());
    }

    // O fim da tarefa da entrada (revisão final, A-08): se ainda estiver «à espera» sem entrada nenhuma a correr, sai
    // (a barra não fica presa com um «Cancelar» que já não faz nada); os outros estados ficam como estão.
    [Fact]
    public void Tracker_Settle_LeavesWaiting_AndKeepsEveryOtherState()
    {
        var (tracker, raised) = NewTracker();
        tracker.Settle();
        Assert.Equal(0, raised());
        tracker.Begin("ana.teste@example.com");
        tracker.Settle();
        Assert.Equal(SignInStage.Idle, tracker.Current.Stage);
        Assert.Null(tracker.LastHint);
        Assert.Equal(2, raised());

        tracker.Begin(null);
        tracker.SignedIn();
        tracker.Settle();
        Assert.Equal(SignInStage.Done, tracker.Current.Stage);
        tracker.Begin("ana.teste@example.com");
        tracker.Failed(new OAuthException("recusado", failure: OAuthFailure.Refused));
        tracker.Settle();
        Assert.Equal(SignInStage.Refused, tracker.Current.Stage);
        Assert.Equal("ana.teste@example.com", tracker.LastHint);
    }

    [Fact]
    public void Tracker_ManualUrl_OnlyWhileWaiting()
    {
        var (tracker, raised) = NewTracker();
        tracker.ShowManualUrl("endereco-1");
        Assert.Equal(SignInProgress.None, tracker.Current); // em Idle não faz nada
        Assert.Equal(0, raised());

        tracker.Begin(null);
        tracker.ShowManualUrl("endereco-1");
        Assert.Equal(SignInStage.Waiting, tracker.Current.Stage);
        Assert.Equal("endereco-1", tracker.Current.ManualUrl);
        Assert.Equal(2, raised());

        tracker.SignedIn();
        Assert.Null(tracker.Current.ManualUrl);
        tracker.ShowManualUrl("endereco-2");
        Assert.Equal(SignInStage.Done, tracker.Current.Stage);
        Assert.Null(tracker.Current.ManualUrl);
        Assert.Equal(3, raised());
    }

    [Fact]
    public void Tracker_SignedIn_ThenFinished_KeepsTheSameAtMs()
    {
        var (tracker, raised) = NewTracker();
        tracker.Begin(null);
        _now += 30_000;
        tracker.SignedIn();
        var at = tracker.Current.AtMs;
        Assert.Equal(_now, at);
        Assert.Equal(SignInStage.Done, tracker.Current.Stage);
        Assert.True(tracker.Current.Ended);
        Assert.Null(tracker.Current.Outcome);

        _now += 5_000;
        tracker.Finished(SignInPageKind.FirstDevice);
        Assert.Equal(SignInStage.Done, tracker.Current.Stage);
        Assert.Equal(SignInPageKind.FirstDevice, tracker.Current.Outcome);
        Assert.Equal(at, tracker.Current.AtMs);
        Assert.Equal(3, raised());

        // Fora de Done, Finished não faz nada.
        var (other, otherRaised) = NewTracker();
        other.Begin(null);
        other.Finished(SignInPageKind.Neutral);
        Assert.Equal(SignInStage.Waiting, other.Current.Stage);
        Assert.Null(other.Current.Outcome);
        Assert.Equal(1, otherRaised());
    }

    // O estado vai pelo nome: o enum é interno e os parâmetros de um teste público não o podem ser.
    public static TheoryData<Exception, string> Failures => new()
    {
        { new OAuthException("prazo", failure: OAuthFailure.TimedOut), nameof(SignInStage.TimedOut) },
        { new OAuthException("sem Drive", failure: OAuthFailure.MissingScope), nameof(SignInStage.DriveDenied) },
        { new OAuthException("recusou", failure: OAuthFailure.Refused), nameof(SignInStage.Refused) },
        { new OAuthException("outra coisa"), nameof(SignInStage.Refused) },
        { new OperationCanceledException(), nameof(SignInStage.Idle) },
        { new TaskCanceledException(), nameof(SignInStage.Idle) },
        { new TaskCanceledException("prazo", new TimeoutException()), nameof(SignInStage.NoNetwork) },
        { new HttpRequestException("sem rede"), nameof(SignInStage.NoNetwork) },
        { new IOException("ligação caiu"), nameof(SignInStage.NoNetwork) },
        { new SocketException(10061), nameof(SignInStage.NoNetwork) },
        { new InvalidOperationException("inesperado"), nameof(SignInStage.Refused) },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public void StageFor_Table(Exception e, string stage)
    {
        var expected = Enum.Parse<SignInStage>(stage);
        Assert.Equal(expected, SignInTracker.StageFor(e));
        var (tracker, _) = NewTracker();
        tracker.Begin(null);
        tracker.Failed(e);
        Assert.Equal(expected, tracker.Current.Stage);
    }

    [Fact]
    public void Tracker_Clear_OnlyClearsTheStateItWasAskedAbout()
    {
        var (tracker, raised) = NewTracker();
        tracker.Begin(null);
        var waitingAt = tracker.Current.AtMs;
        tracker.Clear(waitingAt);
        Assert.Equal(SignInStage.Waiting, tracker.Current.Stage); // em Waiting não faz nada
        Assert.Equal(1, raised());

        _now += 1_000;
        tracker.Failed(new HttpRequestException());
        var oldAt = tracker.Current.AtMs;
        _now += 1_000;
        tracker.Begin(null);
        _now += 1_000;
        tracker.SignedIn();
        Assert.Equal(4, raised());

        tracker.Clear(oldAt); // o temporizador do erro antigo chega atrasado: não apaga o estado novo
        Assert.Equal(SignInStage.Done, tracker.Current.Stage);
        Assert.Equal(4, raised());

        tracker.Clear(tracker.Current.AtMs);
        Assert.Equal(SignInProgress.None, tracker.Current);
        Assert.Equal(5, raised());
        tracker.Clear(oldAt); // idempotente
        Assert.Equal(5, raised());
    }

    [Fact]
    public void Tracker_RemembersTheHint_ForRetry()
    {
        var (tracker, _) = NewTracker();
        tracker.Begin("ana.teste@example.com");
        Assert.Equal("ana.teste@example.com", tracker.LastHint);
        tracker.Failed(new OAuthException("prazo", failure: OAuthFailure.TimedOut));
        Assert.Equal(SignInStage.TimedOut, tracker.Current.Stage);
        Assert.Equal("ana.teste@example.com", tracker.LastHint);

        tracker.Begin(null);
        Assert.Null(tracker.LastHint);
    }

    [Fact]
    public void Tracker_ForgetsTheHint_OnSuccess_OnCancel_AndOnReset()
    {
        var (tracker, _) = NewTracker();
        tracker.Begin("ana.teste@example.com");
        Assert.NotNull(tracker.LastHint);
        tracker.SignedIn();
        Assert.Null(tracker.LastHint);

        tracker.Begin("ana.teste@example.com");
        tracker.Failed(new OperationCanceledException());
        Assert.Null(tracker.LastHint);
        Assert.Equal(SignInStage.Idle, tracker.Current.Stage);

        tracker.Begin("ana.teste@example.com");
        tracker.Failed(new HttpRequestException());
        Assert.Equal(SignInStage.NoNetwork, tracker.Current.Stage);
        Assert.Equal("ana.teste@example.com", tracker.LastHint); // há «Tentar outra vez»
        tracker.Clear(tracker.Current.AtMs);
        Assert.Null(tracker.LastHint);

        // Reset em qualquer estado.
        foreach (var setUp in new Action[]
        {
            () => { },
            () => tracker.Begin("ana.teste@example.com"),
            () => { tracker.Begin("ana.teste@example.com"); tracker.ShowManualUrl("endereco"); },
            () => { tracker.Begin("ana.teste@example.com"); tracker.Failed(new OAuthException("x", failure: OAuthFailure.Refused)); },
            () => { tracker.Begin("ana.teste@example.com"); tracker.SignedIn(); tracker.Finished(SignInPageKind.Resumed); },
        })
        {
            setUp();
            tracker.Reset();
            Assert.Equal(SignInProgress.None, tracker.Current);
            Assert.Null(tracker.LastHint);
        }
    }

    [Fact]
    public void Tracker_CancelAfterSignedIn_GoesBackToIdle()
    {
        var (tracker, raised) = NewTracker();
        tracker.Begin(null);
        tracker.SignedIn();
        tracker.Finished(SignInPageKind.Neutral);
        var before = raised();
        tracker.Failed(new OperationCanceledException());
        Assert.Equal(SignInStage.Idle, tracker.Current.Stage);
        Assert.Equal(before + 1, raised());
    }
}
