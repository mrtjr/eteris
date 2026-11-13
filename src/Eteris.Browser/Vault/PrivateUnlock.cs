using Eteris.Browser.Tabs;

namespace Eteris.Browser.Vault;

// O resultado de «Desbloquear» nas janelas privadas trancadas (MainWindow.PrivateLock.cs).
public enum PrivateUnlockResult
{
    // O Windows confirmou: destrancam todas as janelas privadas trancadas.
    Unlocked,
    // A conta do Windows não tem proteção: destranca e diz que a tranca não protege nada (PrivateLockText.Unprotected).
    UnlockedUnprotected,
    Canceled,
    Failed,
    // Já havia uma confirmação aberta: esta não chegou a pedir nada.
    Busy,
    // Uma tranca (o Windows bloqueou, o computador suspendeu) ou o fecho da janela dona cancelou a pergunta: continua
    // trancada, diga o Windows o que disser.
    Interrupted,
}

// A via de teste da tranca só sabe responder «cancelado» ou «indisponível»: o tipo não tem valor de aceitar, e nenhum gancho
// de teste destranca (a verificação com sucesso prova-se com um verificador falso nos testes unitários).
public enum PrivateUnlockTestMode { Cancel, Unavailable }

// Destrancar as janelas privadas: pede SEMPRE o Windows (Windows Hello ou, sem ele, a palavra-passe do Windows pela CredUI),
// sem janela de graça e sem a porta do cofre — uma confirmação recente do cofre não destranca em silêncio, e a via de teste
// do cofre não chega aqui. Uma confirmação de cada vez; uma tranca a meio da pergunta ganha à resposta.
public sealed class PrivateUnlock(IReauth verifier)
{
    private readonly SemaphoreSlim _one = new(1, 1);

    public IReauth Verifier { get; } = verifier;

    public async Task<PrivateUnlockResult> RequestAsync(nint owner, CancellationToken cancel)
    {
        if (!_one.Wait(0)) return PrivateUnlockResult.Busy;
        try
        {
            if (cancel.IsCancellationRequested) return PrivateUnlockResult.Interrupted;
            ReauthOutcome outcome;
            try { outcome = await Verifier.VerifyAsync(owner, PrivateLockText.HelloMessage, cancel).ConfigureAwait(false); }
            catch (OperationCanceledException) { return PrivateUnlockResult.Interrupted; }
            catch (Exception) { return PrivateUnlockResult.Failed; }
            if (cancel.IsCancellationRequested) return PrivateUnlockResult.Interrupted;
            return outcome switch
            {
                ReauthOutcome.Verified => PrivateUnlockResult.Unlocked,
                ReauthOutcome.NoProtection => PrivateUnlockResult.UnlockedUnprotected,
                ReauthOutcome.Canceled => PrivateUnlockResult.Canceled,
                _ => PrivateUnlockResult.Failed,
            };
        }
        finally
        {
            _one.Release();
        }
    }

    // A tranca das janelas privadas pede SEMPRE o Windows a sério; a via de teste só sabe responder «cancelado» ou
    // «indisponível» (PrivateUnlockTestMode não tem valor de aceitar), e a via de teste do cofre não chega aqui.
    public static IReauth CreateVerifier(PrivateUnlockTestMode? test)
#if ETERIS_TEST_HOOKS
        => test is { } mode ? new TestPrivateUnlockVerifier(mode) : new WindowsReauth();
#else
        => new WindowsReauth();
#endif
}

#if ETERIS_TEST_HOOKS
// Só para as provas ao vivo (ETERIS_TEST_TRANCA_RESPOSTA): responde sem janela nenhuma, e nunca «confirmado».
internal sealed class TestPrivateUnlockVerifier(PrivateUnlockTestMode mode) : IReauth
{
    public PrivateUnlockTestMode Mode { get; } = mode;

    public ReauthAvailability CheckAvailability() => Mode == PrivateUnlockTestMode.Cancel ? ReauthAvailability.Hello : ReauthAvailability.WindowsPassword;

    public Task<ReauthOutcome> VerifyAsync(nint owner, string message, CancellationToken cancel)
        => Task.FromResult(Mode == PrivateUnlockTestMode.Cancel ? ReauthOutcome.Canceled : ReauthOutcome.Failed);
}
#endif
