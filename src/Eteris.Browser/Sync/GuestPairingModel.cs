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
    // Alerta «conta substituída» (A1; o título vai a negrito).
    public const string ReplacedTitle = "A conta de sincronização na nuvem foi apagada ou substituída noutro computador.";
    public const string ReplacedText = "Se não foi você, alguém pode ter entrado na sua conta Google: mude a palavra-passe da Google e use «Retirar o acesso do Éteris». Os dados deste computador estão intactos.";
    public const string ReplacedJoin = "Foi eu — juntar este computador";
    public const string ReplacedNotMe = "Não fui eu";
    // PROPOSTA (P2): a consequência de «Foi eu», por baixo dos botões.
    public const string ReplacedConsequence = "Se juntar este computador, as palavras-passe e os outros dados dele passam a ser enviados para a conta que está agora na nuvem.";
    // PROPOSTA (P5): o botão de ícone do cartão (dica e nome acessível), como o da barra do início de sessão.
    public const string Close = "Fechar";

    public static GuestPairingView From(SyncStatus? status)
    {
        if (status is null) return GuestPairingView.None;
        if (status.ChainReplaced) return new(GuestPairingKind.Replaced);
        return GuestPairingView.None;
    }

    // O cartão quer a vaga 3: há estado para mostrar (não é None nem Replaced) e não está recolhido.
    public static bool CardWants(GuestPairingView view, bool cardOpen)
        => cardOpen && view.Kind is not (GuestPairingKind.None or GuestPairingKind.Replaced);

    // O alerta quer a vaga 2: a conta foi substituída e ainda não se carregou em «Não fui eu».
    public static bool ReplacedWants(GuestPairingView view, bool dismissed) => view.Kind == GuestPairingKind.Replaced && !dismissed;

    // A barra do início de sessão cala «Falta confirmar…» e «A sincronizar…»: o cartão (ou o alerta) trata deste estado.
    public static bool Handles(GuestPairingView view, bool cardOpen) => view.Kind == GuestPairingKind.Replaced || CardWants(view, cardOpen);
}
