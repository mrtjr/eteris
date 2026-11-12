using System.IO;
using System.Windows.Threading;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

public partial class MainWindow
{
    // Um só ficheiro e um só temporizador para o processo: todas as janelas normais gravam a sessão de todas.
    private static SessionStore? s_sessionFile;
    private static SessionStore SessionFile => s_sessionFile ??= new(Path.Combine(AppPaths.Root, "session.json"));
    // A descarga para o disco custa milissegundos (docs/motor.md F5): o temporizador grava fora do fio da janela e só a
    // última versão interessa; o fecho da última janela e a saída gravam já (WriteNow) e esperam. Uma falha avisa-se no
    // fio da janela com InvokeAsync, nunca Invoke: o WriteNow espera pela escrita em curso a partir desse fio.
    private static readonly LatestWriter<IReadOnlyList<Data.SessionWindow>> s_sessionWriter = NewSessionWriter();

    private static LatestWriter<IReadOnlyList<Data.SessionWindow>> NewSessionWriter()
    {
        var writer = new LatestWriter<IReadOnlyList<Data.SessionWindow>>(windows => SessionFile.TrySaveWindows(windows));
        writer.Failed += () => System.Windows.Application.Current?.Dispatcher.InvokeAsync(WarnSessionNotSaved);
        return writer;
    }
    private static DispatcherTimer? s_sessionTimer;
    // A saída («Atualizar para…», encerramento do Windows) já gravou a sessão completa: os fechos seguintes, janela a
    // janela, não a reduzem.
    private static bool s_sessionFrozen;
    private static DispatcherTimer? s_unfreezeTimer;

    // Restaura as abas da última sessão; um endereço passado ao abrir junta-se no fim e fica ativo.
    // Todas as janelas normais gravam a sessão e, com «Continuar onde parei», todas voltam: a dona (a 1.ª) repõe as
    // suas abas logo e as outras depois da 1.ª página (RestoreOtherWindows), cada uma com as suas abas e a sua posição.
    private static bool s_sessionOwnerTaken;
    private bool _ownsSession;
    // As outras janelas da sessão, à espera da 1.ª página; até voltarem a sessão não se grava (o ficheiro com todas
    // as janelas não pode ser substituído pelo da dona sozinha).
    private static IReadOnlyList<Data.SessionWindow>? s_windowsToRestore;

    // Só a primeira janela normal (a dona) lê a sessão e corre o plano de arranque; uma janela reposta traz a sua
    // («restored»); todas as janelas normais a gravam; janelas privadas nunca.
    private void StartTabs(string? initialUrl, Data.SessionWindow? restored)
    {
        _ownsSession = !IsPrivate && restored is null && !s_sessionOwnerTaken;
        if (_ownsSession) s_sessionOwnerTaken = true;

        PendingSplit? restoredSplit = null;
        // Depois de um arranque falhado com o sandbox (INF-01), as abas voltam todas a dormir e abre-se uma nova aba: uma
        // página que faça cair processos do motor não volta a correr sozinha. O Begin já correu (CefConfigurator.WaitReady).
        // Vale também para as outras janelas repostas: cada uma passa por aqui e lê o mesmo StartupGuard.
        var afterFailedStart = Infrastructure.StartupGuard.Current.PreviousStartFailed;
        var previous = restored?.Tabs ?? (_ownsSession && Data.Settings.Current.Startup == "restore" ? SessionFile.Load() : null);
        if (previous is not null)
        {
            Tabs.Restore(previous.Urls, previous.ActiveIndex, previous.Titles, previous.Pinned, previous.GroupIds, previous.Groups, previous.Metas,
                wakeActive: !afterFailedStart);
            // Os outros Espaços ficam na reserva, a dormir, com a sua aba ativa e o seu ecrã dividido.
            Tabs.RestoreWorkspaces(previous.Spaces, previous.SpaceIndex);
            // Nesse arranque o ecrã dividido não se repõe (acordaria as duas abas).
            if (!afterFailedStart) restoredSplit = SplitTabs(previous.Split);
            if (_ownsSession && previous.Windows is { Count: > 0 } others)
            {
                s_windowsToRestore = others;
                FirstPaint.App.Then(RestoreOtherWindows);
            }
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

        // Um ficheiro do perfil que estava danificado avisa-se uma vez, quando a janela carrega (NotifyProfile).
        if (_ownsSession)
        {
            if (AtomicFile.Notice("settings.json", Data.Settings.LoadOutcome) is { } settingsNotice) NotifyProfile(settingsNotice);
            if (AtomicFile.Notice("session.json", SessionFile.LastLoad) is { } sessionNotice) NotifyProfile(sessionNotice);
        }
        if (!IsPrivate) Loaded += (_, _) => ShowProfileNotices();
        if (!IsPrivate) WatchSessionChanges();
    }

    // As abas das janelas que ainda vão voltar: para a varredura do «esquecer ao fechar» não apagar os sites delas.
    private static IEnumerable<string> AddressesToRestore()
        => s_windowsToRestore?.SelectMany(w => SessionStore.Addresses(w.Tabs)) ?? [];

    // Depois da 1.ª página (não atrasa o arranque), sem roubar o foco à dona. Uma janela que falhe não impede as outras.
    private static void RestoreOtherWindows()
    {
        foreach (var entry in s_windowsToRestore ?? [])
        {
            try { new MainWindow(null, false, entry) { ShowActivated = false }.Show(); }
            catch (Exception) { }
        }
        s_windowsToRestore = null;
        ArmSessionTimer();
    }

    private void WatchSessionChanges()
    {
        Tabs.Tabs.CollectionChanged += (_, _) => ScheduleSessionSave();
        Tabs.PropertyChanged += (_, _) => ScheduleSessionSave();
        // A última janela normal a fechar grava já, com ela; com outras abertas, a que fecha sai da sessão um segundo
        // depois (fica em «Reabrir janela fechada») — nunca se grava uma sessão sem janelas.
        Closing += (_, _) => { if (!HasOtherNormalWindows()) SaveSessionNow(now: true); };
        Closed += (_, _) =>
        {
            // A dona fecha com outras janelas abertas: o papel de dona (posição, abas adiadas) passa à seguinte,
            // senão as adiadas deixavam de voltar.
            if (_ownsSession) HandSessionOver(this);
            if (HasOtherNormalWindows()) ArmSessionTimer();
        };
    }

    // Só as carregadas, como em SaveSessionNow: uma janela reposta que falhou a meio não conta.
    private bool HasOtherNormalWindows() => NormalWindows().Any(w => !ReferenceEquals(w, this) && w.IsLoaded);

    private static void HandSessionOver(MainWindow leaving)
    {
        // Na saída todas as janelas estão a fechar: a herdeira não reescreveria o window.json da dona.
        if (s_sessionFrozen) return;
        s_sessionOwnerTaken = false;
        var heir = System.Windows.Application.Current?.Windows.OfType<MainWindow>()
            .FirstOrDefault(w => !ReferenceEquals(w, leaving) && !w.IsPrivate && w.IsLoaded);
        heir?.TakeOverSession();
    }

    private void TakeOverSession()
    {
        if (_ownsSession || IsPrivate || s_sessionOwnerTaken) return;
        _ownsSession = s_sessionOwnerTaken = true;
        Closing += (_, _) => SavePlacement();
        WatchSnoozed();
        ScheduleSessionSave();
    }

    // Chamado também quando o endereço de uma aba muda (OnTabPropertyChanged).
    private void ScheduleSessionSave()
    {
        if (IsPrivate) return;
        ArmSessionTimer();
    }

    private static void ArmSessionTimer()
    {
        if (s_sessionFrozen || System.Windows.Application.Current?.Dispatcher is not { } dispatcher) return;
        s_sessionTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            s_sessionTimer!.Stop();
            SaveSessionNow();
        }, dispatcher);
        s_sessionTimer.Stop();
        s_sessionTimer.Start();
    }

    // Grava as abas de todas as janelas normais abertas, a dona primeiro. Um session.json só de leitura ou preso por
    // outro programa não fecha o Éteris: a sessão fica por gravar (volta a tentar-se na mudança de aba seguinte) e
    // avisa-se uma vez.
    private static void SaveSessionNow(bool now = false)
    {
        if (s_sessionFrozen) return;
        if (s_windowsToRestore is not null)
        {
            ArmSessionTimer();
            return;
        }
        var windows = SessionExit.Order(NormalWindows().Where(w => w.IsLoaded).ToList(), w => w._ownsSession);
        if (windows.Count == 0) return;
        var snapshot = windows.Select(w => w.SessionSnapshot()).ToList();
        if (now) s_sessionWriter.WriteNow(snapshot);
        else s_sessionWriter.Post(snapshot);
    }

    private Data.SessionWindow SessionSnapshot()
    {
        var active = Tabs.Active is { } tab ? Tabs.Tabs.IndexOf(tab) : 0;
        var (groupIds, groups) = Tabs.GroupSnapshot();
        var (spaces, spaceIndex) = Tabs.WorkspaceSnapshot();
        return new Data.SessionWindow(SessionStore.Compose(Tabs.Tabs.Select(t => t.Address), active, Tabs.Tabs.Select(t => t.Title),
            Tabs.Tabs.Select(t => t.IsPinned), groupIds, groups, SplitSnapshot(), Tabs.MetaSnapshot(), spaces, spaceIndex), PlacementSnapshot());
    }

    // «Atualizar para…» e o encerramento do Windows: grava-se já a sessão completa e os fechos seguintes, janela a
    // janela, já não a reescrevem. Se o Éteris ainda estiver vivo passado SessionExit.UnfreezeAfter, o encerramento
    // foi cancelado e a gravação volta.
    internal static void FreezeSessionForExit()
    {
        SaveSessionNow(now: true);
        s_sessionFrozen = true;
        if (System.Windows.Application.Current?.Dispatcher is not { } dispatcher) return;
        s_unfreezeTimer ??= new DispatcherTimer(SessionExit.UnfreezeAfter, DispatcherPriority.Background, (_, _) =>
        {
            s_unfreezeTimer!.Stop();
            s_sessionFrozen = false;
            ArmSessionTimer();
        }, dispatcher);
        s_unfreezeTimer.Stop();
        s_unfreezeTimer.Start();
    }

    private static bool s_sessionWarned;

    private static void WarnSessionNotSaved()
    {
        if (s_sessionWarned) return;
        s_sessionWarned = true;
        NotifyProfile("Não foi possível guardar a sessão — as abas abertas podem não voltar no próximo arranque");
    }
}
