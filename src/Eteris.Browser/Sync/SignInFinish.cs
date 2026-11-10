using Eteris.Sync.Engine;
using Eteris.Sync.Google;

namespace Eteris.Browser.Sync;

// Que página de fim mostrar depois de a sessão Google ficar iniciada: lê-se do estado da sincronização, que acabou de
// ligar. Puro (sem interface nem rede); quem espera é GoogleSync, com a página do navegador do sistema em espera.
internal static class SignInFinish
{
    internal static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    // null = ainda não se sabe. «hadKey»: a chave já estava guardada antes de ligar (reentrada).
    internal static SignInPageKind? Decide(bool hadKey, SyncStatus? status, bool roundDone)
    {
        if (status is null || status.Phase is SyncPhase.Starting or SyncPhase.Off)
            return roundDone ? SignInPageKind.Neutral : null;
        // Uma falha (ou a autorização por renovar) não promete nada.
        if (status.NeedsSignIn || status.Error is not null) return SignInPageKind.Neutral;
        return status.Phase switch
        {
            SyncPhase.Choose or SyncPhase.Pairing => SignInPageKind.NeedsApproval,
            SyncPhase.On => hadKey ? SignInPageKind.Resumed : SignInPageKind.FirstDevice,
            // Stopped: outra conta Google que a guardada — quem explica é a janela do Éteris, não a página.
            _ => SignInPageKind.Neutral,
        };
    }

    // Repete Decide a cada «poll» (ou quando a ronda acaba); com a ronda acabada sem decisão, a neutra.
    internal static async Task<SignInPageKind> WaitAsync(Func<SyncStatus?> status, Task round, bool hadKey, TimeSpan poll, CancellationToken cancellationToken)
    {
        while (true)
        {
            var done = round.IsCompleted;
            if (Decide(hadKey, status(), done) is { } page) return page;
            if (done) return SignInPageKind.Neutral;
            await Task.WhenAny(round, Task.Delay(poll, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
