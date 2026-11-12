using System.ComponentModel;
using System.Windows;
using Eteris.Browser.Browser;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// «Reabrir janela fechada»: as abas de uma janela normal fechada ficam guardadas enquanto o Éteris estiver aberto.
public partial class MainWindow
{
    private static readonly ClosedWindows s_closedWindows = new();

    // Até quando um segundo pedido de fecho confirma fechar uma janela com abas trancadas.
    private DateTime _lockedCloseConfirmUntil;

    private void RememberWhenClosing()
    {
        Closing += (_, e) =>
        {
            if (e.Cancel) return;
            // A última janela é guardada pela sessão; as privadas não deixam rasto.
            var others = Application.Current.Windows.OfType<MainWindow>().Count(w => !ReferenceEquals(w, this));
            if (IsPrivate || others == 0) return;
            var tabs = Tabs.Tabs.Where(t => !NewTabPage.IsNewTab(t.Address)).ToList();
            var active = Tabs.Active is { } a ? Math.Max(0, tabs.IndexOf(a)) : 0;
            // Os outros Espaços vão junto (a reserva não está na faixa): nada se perde ao fechar a janela. As abas
            // levam o nome próprio, o emoji, a cor, a nota e a tranca.
            var (spaces, spaceIndex) = Tabs.WorkspaceSnapshot();
            s_closedWindows.Push(new ClosedWindow(tabs.Select(t => t.Address).ToList(), tabs.Select(t => t.Title).ToList(), active,
                spaces, spaceIndex, TabsViewModel.MetaSnapshotOf(tabs, Tabs.Active)));
        };
    }

    // Fechar a janela (X, Alt+F4) com abas trancadas que não ficam guardadas na sessão pede confirmação: o primeiro
    // pedido avisa e não fecha; um segundo nos segundos seguintes fecha. Ao sair do Windows (ou do Éteris) o WPF
    // ignora o cancelamento, por isso isto nunca prende o encerramento.
    protected override void OnClosing(CancelEventArgs e)
    {
        // Reiniciar para atualizar fecha cada janela à mão: aí não se pergunta nada (a atualização não pode ficar presa).
        var locked = Updates.UpdateService.RestartRequested || _endingPrivateSession ? 0 : LockedTabsAtRisk();
        if (locked > 0 && DateTime.UtcNow > _lockedCloseConfirmUntil)
        {
            e.Cancel = true;
            _lockedCloseConfirmUntil = DateTime.UtcNow + LockedCloseWindow;
            ShowToast(locked == 1
                ? "Esta janela tem 1 aba trancada — feche outra vez para confirmar"
                : $"Esta janela tem {locked} abas trancadas — feche outra vez para confirmar");
            return;
        }
        base.OnClosing(e);
    }

    private static readonly TimeSpan LockedCloseWindow = TimeSpan.FromSeconds(4);

    // As trancadas (em todos os Espaços) que se perderiam: só a última janela normal, com «Continuar onde parei», fica
    // na sessão; com outra janela normal aberta esta sai dela (também quando é a dona).
    private int LockedTabsAtRisk()
        => SessionExit.LockedAtRisk(IsPrivate, Data.Settings.Current.Startup == "restore",
            NormalWindows().Count(w => !ReferenceEquals(w, this)), Tabs.Workspaces.Sum(w => Tabs.TabsOf(w).Count(t => t.IsLocked)));

    private static void ReopenClosedWindow()
    {
        if (s_closedWindows.Pop() is not { } closed) return;
        // Só com abas nos outros Espaços, a faixa do Espaço à vista abre com uma aba nova.
        var addresses = closed.Addresses.Count > 0 ? closed.Addresses : [NewTabPage.Url];
        var window = OpenWindowWith(addresses, closed.Titles, closed.ActiveIndex);
        if (window is null) return;
        if (closed.Addresses.Count > 0 && closed.Metas is { } metas)
            for (var i = 0; i < metas.Count && i < window.Tabs.Tabs.Count; i++)
            {
                var meta = metas[i];
                window.Tabs.Tabs[i].SetIdentity(meta.Name, meta.Emoji, meta.Color, meta.Note);
                window.Tabs.Tabs[i].IsLocked = meta.Locked;
            }
        window.Tabs.RestoreWorkspaces(closed.Spaces, closed.SpaceIndex);
    }

    // Uma janela nova (do tipo pedido: normal ou privada) com estas abas; só a primeira carrega.
    private static MainWindow? OpenWindowWith(IReadOnlyList<string> addresses, IReadOnlyList<string> titles, int activeIndex,
        bool isPrivate = false)
    {
        if (addresses.Count == 0) return null;
        var window = new MainWindow(addresses[0], isPrivate);
        window.Show();
        for (var i = 1; i < addresses.Count; i++) window.Tabs.AppendSleeping(addresses[i], i < titles.Count ? titles[i] : null);
        if (activeIndex < window.Tabs.Tabs.Count) window.Tabs.Active = window.Tabs.Tabs[activeIndex];
        return window;
    }

    // Sessão guardada: cada aba volta com o nome próprio e a nota que tinha.
    private static void OpenWindowWith(IReadOnlyList<Data.SavedTab> tabs, int activeIndex)
    {
        if (tabs.Count == 0) return;
        if (OpenWindowWith(tabs.Select(t => t.Url).ToList(), tabs.Select(t => t.Title).ToList(), activeIndex) is not { } window) return;
        for (var i = 0; i < tabs.Count && i < window.Tabs.Tabs.Count; i++)
            window.Tabs.Tabs[i].SetIdentity(tabs[i].Name, null, null, tabs[i].Note);
    }
}
