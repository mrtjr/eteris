using Eteris.Browser.Sync;
using Eteris.Sync.Engine;

namespace Eteris.Browser.Tests.Sync;

// O lembrete do kit de recuperação (0.2.15, T5): quando aparece na barra e quando fica só no menu. Política pura.
public class RecoveryReminderTests
{
    private const long Now = 1_762_560_000_000;
    private const long Minute = 60_000;
    private const long Day = 24 * 60 * Minute;

    // Por omissão: por guardar, 1 computador, sincronização ligada, janela normal, não respondida; o Éteris abriu há
    // uma hora e o segredo chegou antes disso (já estava neste PC).
    private static KitInfo Kit(KitState state = KitState.Pending, long since = Now - 2 * Day, long snoozed = 0, bool declined = false, int devices = 1)
        => new("K7QF", state, since, snoozed, declined, devices);

    private static KitReminderView Decide(KitInfo? kit, bool syncOn = true, bool privateWindow = false, long sessionStart = Now - 60 * Minute,
        bool answered = false, long now = Now)
        => RecoveryReminder.Decide(new KitReminderInput(kit, syncOn, privateWindow, sessionStart, answered), now);

    [Fact]
    public void NothingAtCreation()
    {
        var view = Decide(Kit(since: Now - Minute), sessionStart: Now - 60 * Minute);
        Assert.True(view.MenuLine);
        Assert.False(view.Bar);
    }

    [Fact]
    public void Bar_OnTheNextOpening()
    {
        var view = Decide(Kit(since: Now - 2 * 60 * Minute), sessionStart: Now - 60 * Minute);
        Assert.True(view.Bar);
        Assert.True(view.MenuLine);
        Assert.False(view.Stale);
    }

    [Fact]
    public void Bar_After24h_InTheSameSession()
    {
        // O segredo chegou nesta abertura (depois de o Éteris abrir): só 24 h depois, no mesmo processo.
        var since = Now - RecoveryReminder.FirstDelayMs;
        var start = since - Minute;
        Assert.True(Decide(Kit(since: since), sessionStart: start, now: since + RecoveryReminder.FirstDelayMs).Bar);
        Assert.False(Decide(Kit(since: since), sessionStart: start, now: since + RecoveryReminder.FirstDelayMs - 1).Bar);
        Assert.Equal(24L * 60 * 60 * 1000, RecoveryReminder.FirstDelayMs);
    }

    [Fact]
    public void SecondDevice_OnlyTheMenu()
    {
        var view = Decide(Kit(devices: 2));
        Assert.False(view.Bar);
        Assert.True(view.MenuLine);
    }

    [Fact]
    public void Saved_ShowsNothing()
    {
        var kit = Kit(KitState.Saved);
        var view = Decide(kit);
        Assert.False(view.MenuLine);
        Assert.False(view.Bar);
        Assert.False(RecoveryReminder.MenuLine(kit));
        Assert.True(RecoveryReminder.MenuLine(Kit()));
        Assert.False(RecoveryReminder.MenuLine(null));
    }

    [Fact]
    public void Declined_ShowsNothing_Ever()
    {
        foreach (var state in new[] { KitState.Pending, KitState.Stale })
        {
            var view = Decide(Kit(state, declined: true), now: Now + 365 * Day);
            Assert.False(view.MenuLine);
            Assert.False(view.Bar);
            Assert.False(view.Stale);
            Assert.False(RecoveryReminder.MenuLine(Kit(state, declined: true)));
        }
    }

    [Fact]
    public void Later_AlwaysSnoozes30Days()
    {
        Assert.Equal(30, RecoveryReminder.SnoozeDays);
        Assert.Equal(2_592_000_000, RecoveryReminder.SnoozeMs);
        Assert.Equal(Now + 30 * Day, RecoveryReminder.SnoozeUntil(Now));
    }

    [Fact]
    public void Snoozed_NoBarUntilTheDate()
    {
        var until = Now + 5 * Day;
        var before = Decide(Kit(snoozed: until), now: until - 1);
        Assert.False(before.Bar);
        Assert.True(before.MenuLine);
        Assert.True(Decide(Kit(snoozed: until), now: until).Bar);
    }

    [Fact]
    public void Stale_UsesTheOtherText()
    {
        var view = Decide(Kit(KitState.Stale));
        Assert.True(view.Stale);
        Assert.True(view.Bar);
        Assert.True(view.MenuLine);
        Assert.False(Decide(Kit(KitState.Stale, devices: 2)).Bar);
        Assert.False(Decide(Kit()).Stale);
        Assert.Equal("O seu kit de recuperação antigo deixou de valer (um computador foi removido). Guarde o novo.", RecoveryReminder.StaleText);
    }

    [Fact]
    public void OncePerSession_NeverInPrivateWindows_NotWhenSyncIsOff()
    {
        var answered = Decide(Kit(), answered: true);
        Assert.False(answered.Bar);
        Assert.True(answered.MenuLine);
        var inPrivate = Decide(Kit(), privateWindow: true);
        Assert.False(inPrivate.Bar);
        var off = Decide(Kit(), syncOn: false);
        Assert.False(off.Bar);
        Assert.False(off.MenuLine);
        var none = Decide(null);
        Assert.False(none.Bar);
        Assert.False(none.MenuLine);
    }

    [Fact]
    public void Texts_AreThePlanTexts()
    {
        Assert.Equal("Guarde o seu kit de recuperação.", RecoveryReminder.Title);
        Assert.Equal("Se perder este computador, é com ele que recupera, na nuvem, os favoritos e as palavras-passe.", RecoveryReminder.Body);
        Assert.Equal("Guardar o kit", RecoveryReminder.Save);
        Assert.Equal("Mais tarde", RecoveryReminder.Later);
        Assert.Equal("Não quero: aceito que, sem este computador, a cópia na nuvem não se recupera", RecoveryReminder.Decline);
        Assert.Equal("O seu kit de recuperação antigo deixou de valer (um computador foi removido). Guarde o novo.", RecoveryReminder.StaleText);
        Assert.Equal("Guarde o kit de recuperação", RecoveryReminder.MenuText);
    }
}
