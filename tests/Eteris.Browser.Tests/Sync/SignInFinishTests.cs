using Eteris.Browser.Sync;
using Eteris.Sync.Engine;
using Eteris.Sync.Google;

namespace Eteris.Browser.Tests.Sync;

// O que a página de fim diz depois de a sessão ficar iniciada: lido do estado da sincronização, sem prometer nada que
// ainda não se saiba.
public class SignInFinishTests
{
    public static TheoryData<bool, SyncStatus?, bool, SignInPageKind?> Table => new()
    {
        { false, null, false, null },
        { false, null, true, SignInPageKind.Neutral },
        { false, TestStatus.Of(SyncPhase.Off), false, null },
        { false, TestStatus.Of(SyncPhase.Off), true, SignInPageKind.Neutral },
        { false, TestStatus.Of(SyncPhase.Starting), false, null },
        { true, TestStatus.Of(SyncPhase.Starting), true, SignInPageKind.Neutral },
        { false, TestStatus.Of(SyncPhase.On, error: "Sem ligação"), false, SignInPageKind.Neutral },
        { true, TestStatus.Of(SyncPhase.On) with { NeedsSignIn = true }, false, SignInPageKind.Neutral },
        { false, TestStatus.Of(SyncPhase.Choose), false, SignInPageKind.NeedsApproval },
        { false, TestStatus.Of(SyncPhase.Pairing, pairing: new("123 456", false, null)), false, SignInPageKind.NeedsApproval },
        { false, TestStatus.Of(SyncPhase.On, running: true), false, SignInPageKind.FirstDevice },
        { true, TestStatus.Of(SyncPhase.On), true, SignInPageKind.Resumed },
        { false, TestStatus.Of(SyncPhase.Stopped), false, SignInPageKind.Neutral },
        { true, TestStatus.Of(SyncPhase.Stopped), true, SignInPageKind.Neutral },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Decide_Table(bool hadKey, SyncStatus? status, bool roundDone, SignInPageKind? expected)
        => Assert.Equal(expected, SignInFinish.Decide(hadKey, status, roundDone));

    // A conta na nuvem foi apagada ou substituída (T7): a página não promete a aprovação noutro computador, que não vai
    // acontecer sozinha (o alerta explica-o na janela).
    [Fact]
    public void Decide_ChainReplaced_IsNeutral()
    {
        var replaced = TestStatus.Of(SyncPhase.Choose) with { ChainReplaced = true };
        Assert.Equal(SignInPageKind.Neutral, SignInFinish.Decide(hadKey: true, replaced, roundDone: false));
        Assert.Equal(SignInPageKind.Neutral, SignInFinish.Decide(hadKey: false, replaced, roundDone: false));
        Assert.Equal(SignInPageKind.NeedsApproval, SignInFinish.Decide(hadKey: false, TestStatus.Of(SyncPhase.Choose), roundDone: false));
    }

    [Fact]
    public async Task Wait_ReturnsAsSoonAsThePhaseIsKnown_NotAtTheEndOfTheRound()
    {
        var phases = new Queue<SyncPhase>([SyncPhase.Starting, SyncPhase.Starting, SyncPhase.Choose]);
        var round = new TaskCompletionSource();
        var reads = 0;
        var page = await SignInFinish.WaitAsync(() =>
        {
            reads++;
            return TestStatus.Of(phases.Count > 1 ? phases.Dequeue() : phases.Peek());
        }, round.Task, hadKey: false, TimeSpan.FromMilliseconds(1), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SignInPageKind.NeedsApproval, page);
        Assert.Equal(3, reads);
        Assert.False(round.Task.IsCompleted);
    }

    [Fact]
    public async Task Wait_RoundEndsUndecided_IsNeutral()
    {
        var round = new TaskCompletionSource();
        var wait = SignInFinish.WaitAsync(() => TestStatus.Of(SyncPhase.Starting), round.Task, hadKey: true, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.False(wait.IsCompleted);
        round.SetResult(); // a ronda acabou sem sair de «Starting» (não devia, mas a página não fica à espera disso)
        Assert.Equal(SignInPageKind.Neutral, await wait.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
