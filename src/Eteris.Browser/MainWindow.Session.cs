using System.IO;
using System.Windows.Threading;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser;

public partial class MainWindow
{
    private readonly SessionStore _session = new(Path.Combine(AppPaths.Root, "session.json"));
    private DispatcherTimer? _sessionTimer;

    // Restaura as abas da última sessão; um endereço passado ao abrir junta-se no fim e fica ativo.
    private static bool s_sessionOwnerTaken;
    private bool _ownsSession;

    // Só a primeira janela normal restaura e grava a sessão; janelas privadas nunca.
    private void StartTabs(string? initialUrl)
    {
        _ownsSession = !IsPrivate && !s_sessionOwnerTaken;
        if (_ownsSession) s_sessionOwnerTaken = true;

        PendingSplit? restoredSplit = null;
        // Depois de um arranque falhado com o sandbox (INF-01), as abas voltam todas a dormir e abre-se uma nova aba: uma
        // página que faça cair processos do motor não volta a correr sozinha. O Begin já correu (CefConfigurator.WaitReady).
        var afterFailedStart = Infrastructure.StartupGuard.Current.PreviousStartFailed;
        if (_ownsSession && Data.Settings.Current.Startup == "restore" && _session.Load() is { } previous)
        {
            Tabs.Restore(previous.Urls, previous.ActiveIndex, previous.Titles, previous.Pinned, previous.GroupIds, previous.Groups, previous.Metas,
                wakeActive: !afterFailedStart);
            // Os outros Espaços ficam na reserva, a dormir, com a sua aba ativa e o seu ecrã dividido.
            Tabs.RestoreWorkspaces(previous.Spaces, previous.SpaceIndex);
            // Nesse arranque o ecrã dividido não se repõe (acordaria as duas abas).
            if (!afterFailedStart) restoredSplit = SplitTabs(previous.Split);
        }

        var version = Browser.SettingsPage.Version;
        var planActivated = false;
        if (_ownsSession)
        {
            var plan = Browser.StartupPages.Plan(Data.Settings.IsFirstRun, Data.Settings.Current.LastSeenVersion, version, initialUrl,
                hasRestoredTabs: Tabs.Tabs.Count > 0 && !afterFailedStart);
            if (plan.Any(t => t.Url == Browser.WelcomePage.Url || t.Url == Browser.WhatsNewPage.Url)) Data.Settings.MarkVersionSeen(version);
            foreach (var tab in plan) Tabs.NewTab(tab.Url, tab.Activate);
            planActivated = plan.Any(t => t.Activate);
        }
        // Depois do plano: uma aba ativada por ele já não desfaz a divisão reposta (ela fica à espera).
        if (restoredSplit is not null) RestoreSplitAtStartup(restoredSplit, planActivated);
        // A dona da sessão já abriu o endereço pelo plano de arranque; só as outras janelas o abrem aqui.
        else if (Browser.StartupPages.OpensInitialUrlSeparately(_ownsSession, initialUrl)) Tabs.NewTab(initialUrl!);
        else if (Tabs.Tabs.Count == 0)
        {
            Tabs.NewTab(NewTabUrl);
            Loaded += (_, _) => FocusOmnibox();
        }

        if (!_ownsSession) return;
        WatchSessionChanges();
    }

    private void WatchSessionChanges()
    {
        Tabs.Tabs.CollectionChanged += (_, _) => ScheduleSessionSave();
        Tabs.PropertyChanged += (_, _) => ScheduleSessionSave();
        Closing += (_, _) => SaveSession();
        // A dona fecha com outras janelas abertas: a sessão (gravação, posição, abas adiadas) passa à seguinte,
        // senão as adiadas deixavam de voltar e o que se fizesse nas outras janelas não ficava guardado.
        Closed += (_, _) => HandSessionOver(this);
    }

    private static void HandSessionOver(MainWindow leaving)
    {
        s_sessionOwnerTaken = false;
        var heir = System.Windows.Application.Current?.Windows.OfType<MainWindow>()
            .FirstOrDefault(w => !ReferenceEquals(w, leaving) && !w.IsPrivate && w.IsLoaded);
        heir?.TakeOverSession();
    }

    private void TakeOverSession()
    {
        if (_ownsSession || IsPrivate || s_sessionOwnerTaken) return;
        _ownsSession = s_sessionOwnerTaken = true;
        WatchSessionChanges();
        Closing += (_, _) => SavePlacement();
        WatchSnoozed();
        ScheduleSessionSave();
    }

    // Chamado também quando o endereço de uma aba muda (OnTabPropertyChanged).
    private void ScheduleSessionSave()
    {
        if (!_ownsSession) return;
        _sessionTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            _sessionTimer!.Stop();
            SaveSession();
        }, Dispatcher);
        _sessionTimer.Stop();
        _sessionTimer.Start();
    }

    private void SaveSession()
    {
        try
        {
            var active = Tabs.Active is { } tab ? Tabs.Tabs.IndexOf(tab) : 0;
            var (groupIds, groups) = Tabs.GroupSnapshot();
            var (spaces, spaceIndex) = Tabs.WorkspaceSnapshot();
            _session.Save(Tabs.Tabs.Select(t => t.Address), active, Tabs.Tabs.Select(t => t.Title), Tabs.Tabs.Select(t => t.IsPinned),
                groupIds, groups, SplitSnapshot(), Tabs.MetaSnapshot(), spaces, spaceIndex);
        }
        catch (IOException) { }
    }
}
