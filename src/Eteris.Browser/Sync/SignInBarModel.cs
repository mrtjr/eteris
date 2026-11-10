using Eteris.Sync.Engine;

namespace Eteris.Browser.Sync;

// A barra do início de sessão na janela (0.2.15): o que diz e que botões tem, a partir do estado do início de sessão
// (SignInTracker) e do da sincronização. Decisão pura, sem WPF (testada em SignInBarModelTests).
internal enum SignInBarKind { Waiting, NoBrowser, TimedOut, Refused, DriveDenied, NoNetwork, OtherAccount, Syncing, Synced, NeedsApproval, Expired, KitReminder }

internal enum SignInBarAction { Cancel, CopyAddress, Retry, OpenSettings, ShowCode, Close, SignInAgain, NotNow, SaveKit, KitLater, KitDecline }

internal sealed record SignInBarButton(string Label, SignInBarAction Action, bool Primary = false);

// Text: a frase inteira (também é o nome acessível). Bold: o trecho de Text a negrito (o nome), ou null.
// HideAfter: null = fica até o estado mudar.
internal sealed record SignInBarView(SignInBarKind Kind, string Text, string? Bold, IReadOnlyList<SignInBarButton> Buttons, TimeSpan? HideAfter);

internal sealed record SignInBarInput(SignInProgress Progress, SyncStatus? Sync, string? Name, bool IsPrivate)
{
    // Ganchos do estado Synced (T10). Sem eles, «Sincronizado.» sem botões.
    public string? SyncedText { get; init; }          // substitui «Sincronizado.» (o aviso «Recebeu…» do computador novo)
    public SignInBarButton? SyncedLink { get; init; } // botão discreto («Também pagamentos e documentos…» no 1.º computador)
    // O cartão do computador novo (ou o alerta «conta substituída») trata do estado (T7): a barra não diz «Falta
    // confirmar no outro computador» nem «A sincronizar…».
    public bool GuestPairing { get; init; }
}

internal static class SignInBarModel
{
    // PROPOSTAS (o plano só diz que «Sincronizado.» some sozinha; o dono decide): 4 s sem ligação, 12 s com ela (dá
    // tempo de ler e carregar), 20 s para os erros (nenhum estado fica preso para sempre).
    internal static readonly TimeSpan SyncedHold = TimeSpan.FromSeconds(4);
    internal static readonly TimeSpan SyncedLinkHold = TimeSpan.FromSeconds(12);
    internal static readonly TimeSpan ErrorHold = TimeSpan.FromSeconds(20);

    private const int NameLimit = 40;

    private static readonly SignInBarButton Cancel = new("Cancelar", SignInBarAction.Cancel);
    private static readonly SignInBarButton Retry = new("Tentar outra vez", SignInBarAction.Retry, Primary: true);
    private static readonly SignInBarButton Close = new("Fechar", SignInBarAction.Close);

    internal static SignInBarView? For(SignInBarInput input)
    {
        var progress = input.Progress;
        if (input.IsPrivate) return null;
        switch (progress.Stage)
        {
            case SignInStage.Idle:
                return null;
            case SignInStage.Waiting:
                return progress.ManualUrl is null
                    ? new(SignInBarKind.Waiting, "À espera do início de sessão no seu navegador…", null, [Cancel], null)
                    : new(SignInBarKind.NoBrowser, "Abra este endereço noutro navegador para entrar na Google.", null,
                        [new("Copiar endereço", SignInBarAction.CopyAddress, Primary: true), Cancel], null);
            case SignInStage.TimedOut:
                return Failure(SignInBarKind.TimedOut, "O início de sessão demorou demasiado.");
            case SignInStage.Refused:
                return Failure(SignInBarKind.Refused, "A Google não deu autorização ao Éteris.");
            case SignInStage.DriveDenied:
                return Failure(SignInBarKind.DriveDenied, "Entrou, mas sem a permissão do Drive o Éteris não sincroniza.");
            case SignInStage.NoNetwork:
                // Sem texto no plano: o que o produto já diz neste caso (PROPOSTA de texto).
                return Failure(SignInBarKind.NoNetwork, "Sem ligação à Google. Tente de novo.");
            case SignInStage.Done:
                return Done(input);
            default:
                return null;
        }
    }

    // A sessão expirada (T11): a Google pediu para entrar outra vez. Nunca em janelas privadas; sem o e-mail (o
    // «Entrar» leva-o só no endereço de autorização, como login_hint); fica até ser respondida ou a sessão voltar.
    private static readonly SignInBarView ExpiredView = new(SignInBarKind.Expired,
        "A sincronização está em pausa: a Google pediu para entrar outra vez.", null,
        [new("Entrar", SignInBarAction.SignInAgain, Primary: true), new("Agora não", SignInBarAction.NotNow)], null);

    internal static SignInBarView? Expired(bool expired, bool isPrivate) => isPrivate || !expired ? null : ExpiredView;

    // O lembrete do kit de recuperação (T5): sempre os três botões, por esta ordem; fica até ser respondido. As mesmas
    // instâncias a cada pedido (o controlo não refaz os botões nem repete o anúncio).
    private static readonly SignInBarButton[] KitButtons =
    [
        new(RecoveryReminder.Save, SignInBarAction.SaveKit, Primary: true),
        new(RecoveryReminder.Later, SignInBarAction.KitLater),
        new(RecoveryReminder.Decline, SignInBarAction.KitDecline),
    ];

    private static readonly SignInBarView KitView = new(SignInBarKind.KitReminder,
        RecoveryReminder.Title + " " + RecoveryReminder.Body, RecoveryReminder.Title, KitButtons, null);

    private static readonly SignInBarView KitStaleView = new(SignInBarKind.KitReminder, RecoveryReminder.StaleText, null, KitButtons, null);

    internal static SignInBarView? KitReminder(KitReminderView? view)
        => view is not { Bar: true } ? null : view.Stale ? KitStaleView : KitView;

    private static SignInBarView Failure(SignInBarKind kind, string text) => new(kind, text, null, [Retry], ErrorHold);

    private static SignInBarView? Done(SignInBarInput input)
    {
        var sync = input.Sync;
        if (sync?.OtherAccount == true)
            return new(SignInBarKind.OtherAccount, "Entrou com outra conta Google. A sincronização ficou em pausa para não misturar contas.", null,
                [new("Abrir Definições", SignInBarAction.OpenSettings, Primary: true), Close], null);
        if (sync?.Phase is SyncPhase.Choose or SyncPhase.Pairing)
            return input.GuestPairing ? null : Named(SignInBarKind.NeedsApproval, input.Name, "Falta confirmar no outro computador.",
                [new("Ver código", SignInBarAction.ShowCode, Primary: true), Close]);
        // A ronda falhou (ou a autorização acabou): o menu já o diz; a barra não inventa um texto que o plano não deu.
        if (sync is { Error: not null } or { NeedsSignIn: true }) return null;
        if (sync is { Phase: SyncPhase.On, Running: false } && sync.LastSuccessMs >= input.Progress.AtMs)
            return new(SignInBarKind.Synced, input.SyncedText ?? "Sincronizado.", null,
                input.SyncedLink is null ? [] : [input.SyncedLink],
                input.SyncedLink is null ? SyncedHold : SyncedLinkHold);
        if (sync is null or { Phase: SyncPhase.Starting or SyncPhase.On })
            return input.GuestPairing ? null : Named(SignInBarKind.Syncing, input.Name, "A sincronizar…", []);
        return null; // desligada, ou parada por outra razão
    }

    // «Sessão iniciada como Nome. …», com o nome a negrito; sem nome, «Sessão iniciada. …».
    private static SignInBarView Named(SignInBarKind kind, string? name, string rest, IReadOnlyList<SignInBarButton> buttons)
    {
        var shown = name?.Trim() is { Length: > 0 } clean
            ? clean.Length > NameLimit ? clean[..(NameLimit - 1)] + "…" : clean
            : null;
        var text = shown is null ? $"Sessão iniciada. {rest}" : $"Sessão iniciada como {shown}. {rest}";
        return new(kind, text, shown, buttons, null);
    }
}

// O árbitro «uma barra de cada vez»: o número mais baixo ganha.
internal enum WindowBar { PairingRequest = 1, AccountReplaced = 2, SignIn = 3, SessionExpired = 4, KitReminder = 5 }

internal static class WindowBars
{
    internal static WindowBar? Pick(IEnumerable<WindowBar> wanting)
    {
        WindowBar? best = null;
        foreach (var bar in wanting)
            if (best is null || bar < best) best = bar;
        return best;
    }
}

// O ponto único que mostra ou esconde as barras de uma janela. Um por janela; só na thread da IU; sem WPF.
// Contrato das candidatas: «wants» é barato e seguro (sem E/S, sem criar o agente da sincronização, sem lançar);
// «show(false)» esconde sem criar nada e «show(true)» cria o controlo no primeiro uso, ambos idempotentes; um «show»
// nunca volta a chamar Refresh de forma síncrona; quem regista chama Refresh sempre que o seu «wants» pode ter mudado.
internal sealed class WindowBarSet
{
    private readonly SortedDictionary<WindowBar, (Func<bool> Wants, Action<bool> Show)> _bars = new();
    private bool _refreshing;

    public void Register(WindowBar kind, Func<bool> wants, Action<bool> show)
    {
        if (!_bars.TryAdd(kind, (wants, show)))
            throw new InvalidOperationException($"A barra {kind} já está registada nesta janela.");
    }

    // Pergunta a todas (uma vez cada), escolhe a vencedora, esconde primeiro todas as outras (por ordem de número) e só
    // no fim mostra a vencedora: duas candidatas podem partilhar o mesmo controlo. Reentrante: não faz nada e devolve null.
    public WindowBar? Refresh()
    {
        if (_refreshing) return null;
        _refreshing = true;
        try
        {
            var wanting = _bars.Where(b => b.Value.Wants()).Select(b => b.Key).ToList();
            var winner = WindowBars.Pick(wanting);
            foreach (var (kind, bar) in _bars)
                if (kind != winner) bar.Show(false);
            if (winner is { } shown) _bars[shown].Show(true);
            return winner;
        }
        finally { _refreshing = false; }
    }
}
