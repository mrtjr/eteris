using Eteris.Browser.Tabs;
using Eteris.Browser.Vault;

namespace Eteris.Browser.Tests;

// Destrancar as janelas privadas (Vault/PrivateUnlock.cs): pede SEMPRE o Windows (sem a graça do cofre), uma confirmação
// de cada vez, e uma tranca a meio da pergunta ganha à resposta. A verificação com sucesso só se prova aqui, com um
// verificador falso: nenhum gancho de teste destranca.
public class PrivateUnlockTests
{
    private sealed class FakeReauth(Func<CancellationToken, Task<ReauthOutcome>> answer) : IReauth
    {
        public List<(nint Owner, string Message)> Calls { get; } = [];

        public FakeReauth(ReauthOutcome outcome) : this(_ => Task.FromResult(outcome)) { }

        public ReauthAvailability CheckAvailability() => ReauthAvailability.Hello;

        public Task<ReauthOutcome> VerifyAsync(nint owner, string message, CancellationToken cancel)
        {
            Calls.Add((owner, message));
            return answer(cancel);
        }
    }

    [Fact]
    public async Task Verified_Unlocks()
    {
        var fake = new FakeReauth(ReauthOutcome.Verified);
        Assert.Equal(PrivateUnlockResult.Unlocked, await new PrivateUnlock(fake).RequestAsync(0x10, CancellationToken.None));
        Assert.Single(fake.Calls);
    }

    [Fact]
    public async Task Canceled_StaysLocked()
        => Assert.Equal(PrivateUnlockResult.Canceled, await new PrivateUnlock(new FakeReauth(ReauthOutcome.Canceled)).RequestAsync(0x10, CancellationToken.None));

    [Fact]
    public async Task Failed_StaysLocked()
    {
        Assert.Equal(PrivateUnlockResult.Failed, await new PrivateUnlock(new FakeReauth(ReauthOutcome.Failed)).RequestAsync(0x10, CancellationToken.None));
        var throws = new FakeReauth(_ => Task.FromException<ReauthOutcome>(new InvalidOperationException("o Windows falhou")));
        Assert.Equal(PrivateUnlockResult.Failed, await new PrivateUnlock(throws).RequestAsync(0x10, CancellationToken.None));
        var throwsNow = new FakeReauth(_ => throw new InvalidOperationException("o Windows falhou já"));
        Assert.Equal(PrivateUnlockResult.Failed, await new PrivateUnlock(throwsNow).RequestAsync(0x10, CancellationToken.None));
    }

    [Fact]
    public async Task NoProtection_UnlocksAndSaysTheLockProtectsNothing()
    {
        Assert.Equal(PrivateUnlockResult.UnlockedUnprotected,
            await new PrivateUnlock(new FakeReauth(ReauthOutcome.NoProtection)).RequestAsync(0x10, CancellationToken.None));
        Assert.Contains("não protege nada", PrivateLockText.Unprotected);
        Assert.Contains("PIN", PrivateLockText.Unprotected);
    }

    [Fact]
    public async Task AlwaysAsks_NoGrace()
    {
        var fake = new FakeReauth(ReauthOutcome.Verified);
        var unlock = new PrivateUnlock(fake);
        Assert.Equal(PrivateUnlockResult.Unlocked, await unlock.RequestAsync(0x10, CancellationToken.None));
        Assert.Equal(PrivateUnlockResult.Unlocked, await unlock.RequestAsync(0x10, CancellationToken.None));
        Assert.Equal(2, fake.Calls.Count);
    }

    // O Windows bloqueou (ou a janela dona fechou) com o diálogo aberto: fica trancada, diga o Windows o que disser.
    [Fact]
    public async Task ACancelDuringTheQuestion_StaysLocked_EvenIfWindowsSaysVerified()
    {
        using var cts = new CancellationTokenSource();
        var answer = new TaskCompletionSource<ReauthOutcome>();
        var unlock = new PrivateUnlock(new FakeReauth(_ => answer.Task));
        var pending = unlock.RequestAsync(0x10, cts.Token);
        cts.Cancel();
        answer.SetResult(ReauthOutcome.Verified);
        Assert.Equal(PrivateUnlockResult.Interrupted, await pending);

        var canceled = new FakeReauth(token => Task.FromCanceled<ReauthOutcome>(new CancellationToken(canceled: true)));
        Assert.Equal(PrivateUnlockResult.Interrupted, await new PrivateUnlock(canceled).RequestAsync(0x10, CancellationToken.None));
        using var already = new CancellationTokenSource();
        already.Cancel();
        var notAsked = new FakeReauth(ReauthOutcome.Verified);
        Assert.Equal(PrivateUnlockResult.Interrupted, await new PrivateUnlock(notAsked).RequestAsync(0x10, already.Token));
        Assert.Empty(notAsked.Calls);
    }

    [Fact]
    public async Task OneQuestionAtATime()
    {
        var answer = new TaskCompletionSource<ReauthOutcome>();
        var fake = new FakeReauth(_ => answer.Task);
        var unlock = new PrivateUnlock(fake);
        var first = unlock.RequestAsync(0x10, CancellationToken.None);
        Assert.Equal(PrivateUnlockResult.Busy, await unlock.RequestAsync(0x20, CancellationToken.None));
        Assert.Single(fake.Calls);
        answer.SetResult(ReauthOutcome.Canceled);
        Assert.Equal(PrivateUnlockResult.Canceled, await first);
        // Acabada a 1.ª, pode-se pedir outra vez.
        var again = new PrivateUnlock(new FakeReauth(ReauthOutcome.Verified));
        Assert.Equal(PrivateUnlockResult.Unlocked, await again.RequestAsync(0x10, CancellationToken.None));
    }

    [Fact]
    public async Task PassesTheOwnerAndTheMessage()
    {
        var fake = new FakeReauth(ReauthOutcome.Canceled);
        await new PrivateUnlock(fake).RequestAsync(0x1234, CancellationToken.None);
        Assert.Equal((nint)0x1234, fake.Calls[0].Owner);
        Assert.Equal(PrivateLockText.HelloMessage, fake.Calls[0].Message);
        Assert.Equal("O Éteris quer mostrar as janelas privadas trancadas.", PrivateLockText.HelloMessage);
    }

    // Nenhum modo de teste destranca: o tipo nem tem um valor de aceitar.
    [Fact]
    public async Task NoTestMode_CanUnlock()
    {
        foreach (var mode in Enum.GetValues<PrivateUnlockTestMode>())
        {
            var verifier = new TestPrivateUnlockVerifier(mode);
            var outcome = await verifier.VerifyAsync(0x10, PrivateLockText.HelloMessage, CancellationToken.None);
            Assert.NotEqual(ReauthOutcome.Verified, outcome);
            Assert.NotEqual(ReauthOutcome.NoProtection, outcome);
            Assert.NotEqual(PrivateUnlockResult.Unlocked, await new PrivateUnlock(verifier).RequestAsync(0x10, CancellationToken.None));
            Assert.NotEqual(PrivateUnlockResult.UnlockedUnprotected, await new PrivateUnlock(verifier).RequestAsync(0x10, CancellationToken.None));
        }
        Assert.DoesNotContain(Enum.GetNames<PrivateUnlockTestMode>(), name =>
            new[] { "Accept", "Verif", "Aceit", "Unlock" }.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(ReauthAvailability.Hello, new TestPrivateUnlockVerifier(PrivateUnlockTestMode.Cancel).CheckAvailability());
        Assert.Equal(ReauthOutcome.Canceled, await new TestPrivateUnlockVerifier(PrivateUnlockTestMode.Cancel).VerifyAsync(0, "", CancellationToken.None));
        Assert.Equal(ReauthAvailability.WindowsPassword, new TestPrivateUnlockVerifier(PrivateUnlockTestMode.Unavailable).CheckAvailability());
        Assert.Equal(ReauthOutcome.Failed, await new TestPrivateUnlockVerifier(PrivateUnlockTestMode.Unavailable).VerifyAsync(0, "", CancellationToken.None));
    }

    // O verificador decide-se num só sítio: o de teste só com um modo; sem ele, o do Windows (nunca chamado aqui).
    [Fact]
    public void TheVerifier_IsTheWindowsOneUnlessATestMode()
    {
        Assert.IsType<WindowsReauth>(PrivateUnlock.CreateVerifier(null));
        Assert.IsType<TestPrivateUnlockVerifier>(PrivateUnlock.CreateVerifier(PrivateUnlockTestMode.Cancel));
    }
}
