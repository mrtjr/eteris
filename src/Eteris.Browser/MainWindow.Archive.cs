using System.Windows.Threading;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// Arquivo automático: a cada minuto as abas sem visita há tempo demais desvanecem-se (AgeOpacity) e, vencido o
// prazo das Definições › Abas, saem da faixa para o «Arquivo» (Radar), de onde se repõem. A decisão está em
// Tabs/ArchivePolicy.cs; o repositório em Data/ArchivedTabsStore.cs. Nunca na janela privada.
public partial class MainWindow
{
    private DispatcherTimer? _archiveTimer;

    private void WatchArchive()
    {
        if (IsPrivate) return;
        _archiveTimer = new DispatcherTimer(ArchivePolicy.CheckInterval, DispatcherPriority.Background, (_, _) => ArchiveSweep(), Dispatcher);
        _archiveTimer.Start();
        Dispatcher.InvokeAsync(ArchiveSweep, DispatcherPriority.ApplicationIdle);
        Closed += (_, _) => _archiveTimer.Stop();
    }

    private void ArchiveSweep()
    {
        var now = DateTime.UtcNow;
        var days = Settings.Current.ArchiveDays;
        var archived = new List<BrowserTab>();
        foreach (var tab in Tabs.Tabs.ToList())
        {
            var onScreen = IsTabOnScreen(tab);
            var isNew = NewTabPage.IsNewTab(tab.Address);
            var exempt = ArchivePolicy.IsExempt(onScreen, tab.IsPinned, tab.IsLocked, tab.HasNote, tab.IsAudible, isNew);
            tab.AgeOpacity = ArchivePolicy.Opacity(exempt, tab.LastActiveUtc, now, days);
            if (ArchivePolicy.ShouldArchive(onScreen, tab.IsPinned, tab.IsLocked, tab.HasNote, tab.IsAudible, isNew, tab.LastActiveUtc, now, days))
                archived.Add(tab);
        }
        if (archived.Count == 0) return;
        foreach (var tab in archived)
        {
            AppData.ArchiveTab(tab.Address, tab.Title, tab.CustomTitle, tab.LastActiveUtc, now);
            Tabs.CloseQuietly(tab);
        }
        if (Tabs.Tabs.Count == 0) Tabs.NewTab(NewTabUrl);
        ShowToast(archived.Count == 1
            ? $"«{archived[0].DisplayTitle}» foi para o Arquivo — sem visita há {days} dias (Radar › Arquivo)"
            : $"{archived.Count} abas sem visita há {days} dias foram para o Arquivo (Radar › Arquivo)");
    }

    // Repor: volta como aba nova no fim da faixa, com o nome próprio, e sai do arquivo.
    private void RestoreArchived(ArchivedTab archived, bool activate)
    {
        AppData.ForgetArchived(archived.Id);
        var tab = Tabs.NewTab(archived.Url, activate);
        tab.SetIdentity(archived.Name, null, null);
    }

    private IReadOnlyList<ArchivedTab> ArchivedTabs() => IsPrivate ? [] : AppData.ArchivedTabs();
}
