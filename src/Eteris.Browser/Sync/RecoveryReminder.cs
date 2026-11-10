using Eteris.Sync.Engine;

namespace Eteris.Browser.Sync;

// O que decide o lembrete: o kit (sem segredo), se a sincronização está ligada, se a janela é privada, quando este
// processo começou a usar a sincronização e se a barra já foi respondida nesta abertura.
internal sealed record KitReminderInput(KitInfo? Kit, bool SyncOn, bool PrivateWindow, long SessionStartMs, bool AnsweredThisSession);

// A linha do menu, a barra e qual dos dois textos (o kit guardado deixou de valer).
internal sealed record KitReminderView(bool MenuLine, bool Bar, bool Stale);

// O lembrete do kit de recuperação (0.2.15): nada ao criar a conta; uma barra discreta na abertura seguinte à chegada
// do segredo a este computador, ou 24 h depois, só enquanto houver um único computador na conta; «Mais tarde» adia
// sempre 30 dias e «Não quero…» acaba com ele. Com dois ou mais computadores fica só a linha do menu. Decisão pura,
// relógio injetado (testada em RecoveryReminderTests).
internal static class RecoveryReminder
{
    public const int SnoozeDays = 30;
    public const long SnoozeMs = SnoozeDays * 24L * 60 * 60 * 1000;
    public const long FirstDelayMs = 24L * 60 * 60 * 1000;

    public const string Title = "Guarde o seu kit de recuperação.";
    public const string Body = "Se perder este computador, é com ele que recupera, na nuvem, os favoritos e as palavras-passe.";
    public const string StaleText = "O seu kit de recuperação antigo deixou de valer (um computador foi removido). Guarde o novo.";
    public const string Save = "Guardar o kit";
    public const string Later = "Mais tarde";
    public const string Decline = "Não quero: aceito que, sem este computador, a cópia na nuvem não se recupera";
    public const string MenuText = "Guarde o kit de recuperação";

    // A linha do menu: há kit, não está guardado e não foi recusado (em qualquer número de computadores).
    public static bool MenuLine(KitInfo? kit) => kit is { State: not KitState.Saved, Declined: false };

    public static KitReminderView Decide(KitReminderInput input, long nowMs)
    {
        if (!input.SyncOn || !MenuLine(input.Kit)) return new(false, false, false);
        var kit = input.Kit!;
        var bar = kit.KnownDevices <= 1
            && !input.PrivateWindow
            && !input.AnsweredThisSession
            && nowMs >= kit.SnoozedUntilMs
            // O segredo já estava neste computador quando o Éteris abriu («na abertura seguinte»), ou chegou há 24 h.
            && (kit.SinceMs < input.SessionStartMs || nowMs - kit.SinceMs >= FirstDelayMs);
        return new(true, bar, kit.State == KitState.Stale);
    }

    public static long SnoozeUntil(long nowMs) => nowMs + SnoozeMs;
}
