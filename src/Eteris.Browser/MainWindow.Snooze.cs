using System.Windows;
using System.Windows.Threading;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// «Adiar até…»: a aba sai da faixa e volta à hora escolhida (um temporizador de 1 min na janela dona da sessão
// traz as vencidas de volta, a dormir, com aviso). As opções e os textos vêm de Tabs/Snooze.cs; o que está adiado
// vive em Data/SnoozedTabsStore.cs e vê-se no Radar («Adiadas»), de onde se traz já. Nunca na janela privada.
public partial class MainWindow
{
    private DispatcherTimer? _snoozeTimer;

    // Menu com as horas possíveis, por baixo da aba (ou do ponto do rato, vindo da paleta).
    private void OpenSnoozeMenu(BrowserTab tab, UIElement anchor)
    {
        if (IsPrivate || !Tabs.Tabs.Contains(tab)) return;
        var now = DateTime.Now;
        var menu = new DarkMenu();
        foreach (var option in Snooze.Options(now))
        {
            var chosen = option;
            menu.Item("", $"{chosen.Label}  ·  {chosen.When}", () => SnoozeTab(tab, chosen.DueLocal));
        }
        menu.OpenBelow(anchor);
    }

    private void SnoozeTab(BrowserTab tab, DateTime dueLocal)
    {
        if (IsPrivate || !Tabs.Tabs.Contains(tab) || NewTabPage.IsNewTab(tab.Address)) return;
        // Sem gravar, a aba não sai: senão sumia sem voltar à hora prometida.
        if (!AppData.SnoozeTab(tab.Address, tab.Title, tab.CustomTitle, tab.Note, dueLocal.ToUniversalTime()))
        {
            ShowToast("Não foi possível adiar — a base de dados do perfil não está acessível; a aba fica na faixa");
            return;
        }
        Tabs.CloseQuietly(tab);
        if (Tabs.Tabs.Count == 0) Tabs.NewTab(NewTabUrl);
        ShowToast($"«{tab.DisplayTitle}» volta {Snooze.Describe(dueLocal, DateTime.Now)} — ver em Radar › Adiadas");
    }

    // Só a janela dona da sessão vigia (senão duas janelas traziam a mesma aba); começa logo, para as que venceram
    // com o Éteris fechado.
    private void WatchSnoozed()
    {
        if (!_ownsSession) return;
        _snoozeTimer = new DispatcherTimer(Snooze.CheckInterval, DispatcherPriority.Background, (_, _) => WakeDueTabs(), Dispatcher);
        _snoozeTimer.Start();
        Dispatcher.InvokeAsync(WakeDueTabs, DispatcherPriority.ApplicationIdle);
        Closed += (_, _) => _snoozeTimer.Stop();
    }

    // Adiadas já trazidas de volta mas cuja linha a base de dados não deixou apagar: não voltam outra vez.
    private static readonly WokenSnoozes s_wokenSnoozes = new();

    private void WakeDueTabs()
    {
        var due = s_wokenSnoozes.Pending(AppData.SnoozedDue(DateTime.UtcNow), s => s.Id);
        if (due.Count == 0) return;
        foreach (var snoozed in due) WakeSnoozed(snoozed, activate: false);
        ShowToast(due.Count == 1 ? $"«{due[0].Name ?? due[0].Title}» voltou — estava adiada até agora" : $"{due.Count} abas adiadas voltaram");
    }

    // A aba volta ao fim da faixa, a dormir, com o nome próprio e a nota que tinha.
    private BrowserTab WakeSnoozed(SnoozedTab snoozed, bool activate)
    {
        if (!AppData.ForgetSnoozed(snoozed.Id)) s_wokenSnoozes.Remember(snoozed.Id);
        var tab = Tabs.AppendSleeping(snoozed.Url, snoozed.Title);
        tab.SetIdentity(snoozed.Name, null, null, snoozed.Note);
        if (activate) Tabs.Active = tab;
        return tab;
    }

    private IReadOnlyList<SnoozedTab> SnoozedTabs() => IsPrivate ? [] : s_wokenSnoozes.Pending(AppData.SnoozedTabs(), s => s.Id);

}
