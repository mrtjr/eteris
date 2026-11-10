using Eteris.Sync.Engine;

namespace Eteris.Browser.Sync;

// O computador novo (o convidado) a entrar na conta (0.2.15, T7): o que o cartão na janela e as Definições mostram, a
// partir do estado da sincronização. Decisão pura, sem WPF (testada em GuestPairingModelTests). Os textos vivem aqui,
// à letra do plano, e são os mesmos na barra e na página.
internal enum GuestPairingKind { None, Replaced, Waiting, Code, Joined, TimeUp, StillWaiting, Refused, Failed }

// Code só vem preenchido em Kind == Code; Confirmed: a pessoa já disse «Sim»; Error: o texto do agente (Refused e
// Failed).
internal sealed record GuestPairingView(GuestPairingKind Kind, string? Code = null, bool Confirmed = false, string? Error = null)
{
    public static readonly GuestPairingView None = new(GuestPairingKind.None);
}

internal static class GuestPairingModel
{
    // «Sim» só conta 2 s depois de o código aparecer (um toque distraído não confirma).
    public const int ConfirmDelayMs = 2_000;

    // O cartão (os textos do plano, §1.2; o título de G1 vai a negrito).
    public const string WaitTitle = "Falta um passo no outro computador.";                                  // G1
    public const string WaitText = "Abra o Éteris num computador onde já entrou. Este ecrã mostra um código quando o outro responder.";   // G1
    public const string MustBeOpen = "O Éteris tem de estar aberto no outro computador.";                   // G7
    public const string TypeThisCode = "Escreva este código no outro computador:";                          // G2
    public const string Yes = "Sim";
    public const string No = "Não";
    public const string WaitingApproval = "À espera da aprovação no outro computador…";                     // E2
    public const string Joined = "Este computador entrou na conta. A sincronizar…";                         // G4
    public const string TimeUp = "O tempo acabou.";                                                         // G5
    public const string StillWaiting = "Ainda à espera?";                                                   // G6
    public const string AskAgain = "Pedir outra vez";
    public const string NoOther = "Não tenho o outro computador à mão";                                     // G8 (ligação)
    // G8, à letra do plano (as 24 palavras da T6 e o código antigo).
    public const string UseKit = "Para entrar sem o outro computador, use o seu kit de recuperação (24 palavras ou o código de 56 caracteres).";
    public const string PasteKit = "Colar o kit";

    // G3: a pergunta por baixo do código.
    public static string Ask(string code) => $"Escreveu {code} no outro computador e ele aceitou?";

    // Alerta «conta substituída» (A1; o título vai a negrito).
    public const string ReplacedTitle = "A conta de sincronização na nuvem foi apagada ou substituída noutro computador.";
    public const string ReplacedText = "Se não foi você, alguém pode ter entrado na sua conta Google: mude a palavra-passe da Google e use «Retirar o acesso do Éteris». Os dados deste computador estão intactos.";
    public const string ReplacedJoin = "Foi eu — juntar este computador";
    public const string ReplacedNotMe = "Não fui eu";
    // PROPOSTA (P2): a consequência de «Foi eu», por baixo dos botões.
    public const string ReplacedConsequence = "Se juntar este computador, as palavras-passe e os outros dados dele passam a ser enviados para a conta que está agora na nuvem.";
    // PROPOSTA (P5): o botão de ícone do cartão (dica e nome acessível), como o da barra do início de sessão.
    public const string Close = "Fechar";

    // Por esta ordem: sem estado; a conta substituída (vem antes de tudo); o pedido aberto (sem código até o outro
    // responder); o fim do pedido; «Ainda à espera?» num computador novo sem pedido; acabou de entrar; nada.
    public static GuestPairingView From(SyncStatus? status)
    {
        if (status is null) return GuestPairingView.None;
        if (status.ChainReplaced) return new(GuestPairingKind.Replaced);
        var pairing = status.Pairing;
        switch (status.Phase)
        {
            case SyncPhase.Pairing:
                return pairing?.Code is { } code ? new(GuestPairingKind.Code, code, pairing.Confirmed) : new(GuestPairingKind.Waiting);
            case SyncPhase.Choose when pairing is { End: not GuestEnd.None }:
                return pairing.End switch
                {
                    GuestEnd.TimeUp => new(GuestPairingKind.TimeUp),
                    GuestEnd.StillWaiting => new(GuestPairingKind.StillWaiting),
                    GuestEnd.Refused => new(GuestPairingKind.Refused, Error: pairing.Error),
                    _ => new(GuestPairingKind.Failed, Error: pairing.Error),
                };
            case SyncPhase.Choose when status.MayAutoPair:
                return new(GuestPairingKind.StillWaiting);
            case SyncPhase.On when status is { JoinedNow: true, Error: null }:
                return new(GuestPairingKind.Joined);
            default:
                return GuestPairingView.None;
        }
    }

    // O cartão quer a vaga 3: há estado para mostrar (não é None nem Replaced) e não está recolhido.
    public static bool CardWants(GuestPairingView view, bool cardOpen)
        => cardOpen && view.Kind is not (GuestPairingKind.None or GuestPairingKind.Replaced);

    // O alerta quer a vaga 2: a conta foi substituída e ainda não se carregou em «Não fui eu».
    public static bool ReplacedWants(GuestPairingView view, bool dismissed) => view.Kind == GuestPairingKind.Replaced && !dismissed;

    // A barra do início de sessão cala «Falta confirmar…» e «A sincronizar…»: o cartão (ou o alerta) trata deste estado.
    public static bool Handles(GuestPairingView view, bool cardOpen) => view.Kind == GuestPairingKind.Replaced || CardWants(view, cardOpen);

    // «Sim» conta?
    public static bool MayConfirm(long codeShownMs, long nowMs) => nowMs - codeShownMs >= ConfirmDelayMs;
}
