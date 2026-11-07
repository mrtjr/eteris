using System.Collections.ObjectModel;

namespace Eteris.Browser.Tabs;

// Espaços (Tabs/Workspace.cs): a faixa (Tabs) mostra só as abas do Espaço à vista. Mudar de Espaço guarda as abas
// atuais na reserva do Espaço (a dormir, com a aba ativa e a ordem) e põe na faixa as do destino; a aba ativa do
// destino acorda pela janela (OnActiveTabChanging). As abas entram na faixa antes de as antigas saírem, para a
// janela nunca ficar sem abas (a última aba a sair fechá-la-ia).
public partial class TabsViewModel
{
    public ObservableCollection<Workspace> Workspaces { get; } = [new Workspace { IsCurrent = true }];

    public TabsViewModel() => Tabs.CollectionChanged += (_, _) => RefreshWorkspaceCounts();

    public Workspace Current => Workspaces.First(w => w.IsCurrent);

    public bool HasWorkspaces => Workspaces.Count > 1;

    private int _workspacesCreated = 1;

    // Novo Espaço (vazio) e, por omissão, muda-se logo para ele — com uma aba nova, para não ficar sem nada.
    public Workspace NewWorkspace(string? name = null, int? colorIndex = null, string? icon = null, bool activate = true,
        string? newTabAddress = null)
    {
        _workspacesCreated++;
        var workspace = new Workspace(Workspace.NormalizeName(name) ?? Workspace.DefaultNameFor(_workspacesCreated),
            colorIndex ?? Workspace.NextColor(Workspaces.Select(w => w.ColorIndex)), icon);
        Workspaces.Add(workspace);
        if (activate) SwitchWorkspace(workspace, newTabAddress);
        return workspace;
    }

    // Devolve false quando já era o Espaço à vista (ou não é desta janela).
    public bool SwitchWorkspace(Workspace target, string? newTabAddress = null)
    {
        var current = Current;
        if (ReferenceEquals(target, current) || !Workspaces.Contains(target)) return false;

        // 1. A faixa vai para a reserva do Espaço que sai.
        var leaving = Tabs.ToList();
        current.StoredTabs.Clear();
        current.StoredTabs.AddRange(leaving);
        current.StoredActiveIndex = Active is { } active ? Math.Max(0, leaving.IndexOf(active)) : 0;
        current.IsCurrent = false;
        foreach (var tab in leaving)
        {
            tab.IsMultiSelected = false;
            tab.IsSleeping = true;
        }

        // 2. As abas do destino entram (a dormir) e a sua ativa acorda; um Espaço vazio ganha uma aba nova.
        var arriving = target.StoredTabs.ToList();
        var activeIndex = Math.Clamp(target.StoredActiveIndex, 0, Math.Max(0, arriving.Count - 1));
        target.StoredTabs.Clear();
        target.IsCurrent = true;
        // A ativa entra já acordada (a janela cria-lhe o motor ao entrar na faixa, como no restauro da sessão).
        if (arriving.Count > 0) arriving[activeIndex].IsSleeping = false;
        foreach (var tab in arriving) Tabs.Add(tab);
        if (arriving.Count > 0) Active = arriving[activeIndex];
        else NewTab(newTabAddress ?? Browser.NewTabPage.Url);

        // 3. Só agora as abas do Espaço que saiu deixam a faixa (a janela liberta os motores delas).
        foreach (var tab in leaving) Tabs.Remove(tab);
        RefreshGroups();
        RefreshWorkspaceCounts();
        return true;
    }

    public bool SwitchWorkspaceByNumber(int number, string? newTabAddress = null)
    {
        var index = Workspace.IndexForNumber(number, Workspaces.Count);
        return index >= 0 && SwitchWorkspace(Workspaces[index], newTabAddress);
    }

    // Espaço seguinte/anterior, a dar a volta.
    public bool SwitchWorkspaceOffset(int offset, string? newTabAddress = null)
    {
        if (Workspaces.Count < 2) return false;
        var index = (Workspaces.IndexOf(Current) + offset % Workspaces.Count + Workspaces.Count) % Workspaces.Count;
        return SwitchWorkspace(Workspaces[index], newTabAddress);
    }

    // A aba sai da faixa (a dormir, sem grupo nem seleção) para o fim da reserva do destino — fixadas junto às
    // fixadas. Se era a última, fica uma aba nova no lugar dela; se era a ativa, ativa-se a vizinha.
    public bool MoveToWorkspace(BrowserTab tab, Workspace target, string? newTabAddress = null)
    {
        if (!Tabs.Contains(tab) || target.IsCurrent || !Workspaces.Contains(target)) return false;
        if (Tabs.Count == 1) NewTab(newTabAddress ?? Browser.NewTabPage.Url, activate: false);
        var index = Tabs.IndexOf(tab);
        var wasActive = ReferenceEquals(tab, Active);
        tab.Group = null;
        tab.IsMultiSelected = false;
        tab.IsSleeping = true;
        if (wasActive) Active = Tabs[index == Tabs.Count - 1 ? index - 1 : index + 1];
        Tabs.RemoveAt(index);
        RefreshGroups();

        var stored = target.StoredTabs;
        if (tab.IsPinned) stored.Insert(stored.Count(t => t.IsPinned), tab);
        else stored.Add(tab);
        RefreshWorkspaceCounts();
        return true;
    }

    // Apagar um Espaço fecha as abas dele (vão para as fechadas recentes, para nada se perder); o último não se
    // apaga. Apagar o Espaço à vista muda primeiro para o vizinho.
    public bool RemoveWorkspace(Workspace workspace)
    {
        if (Workspaces.Count < 2 || !Workspaces.Contains(workspace)) return false;
        if (workspace.IsCurrent)
        {
            var index = Workspaces.IndexOf(workspace);
            SwitchWorkspace(Workspaces[index > 0 ? index - 1 : index + 1]);
        }
        foreach (var tab in workspace.StoredTabs)
        {
            if (Browser.NewTabPage.IsNewTab(tab.Address)) continue;
            _closed.Add(new ClosedTab(Tabs.Count, tab.Address, tab.Title));
            if (_closed.Count > MaxClosed) _closed.RemoveAt(0);
        }
        workspace.StoredTabs.Clear();
        Workspaces.Remove(workspace);
        RefreshWorkspaceCounts();
        return true;
    }

    // As abas de um Espaço: as da faixa, se está à vista; senão as da reserva.
    public IReadOnlyList<BrowserTab> TabsOf(Workspace workspace) => workspace.IsCurrent ? Tabs : workspace.StoredTabs;

    // Para o Radar: as abas dos Espaços que não estão à vista, pela ordem do seletor e da faixa de cada um.
    public IReadOnlyList<(Workspace Space, BrowserTab Tab)> TabsInOtherWorkspaces()
        => Workspaces.Where(w => !w.IsCurrent).SelectMany(w => w.StoredTabs.Select(t => (w, t))).ToList();

    public Workspace? WorkspaceOf(BrowserTab tab) => Workspaces.FirstOrDefault(w => TabsOf(w).Contains(tab));

    public void RefreshWorkspaceCounts()
    {
        foreach (var workspace in Workspaces) workspace.TabCount = TabsOf(workspace).Count;
    }

    // ---------- Sessão ----------

    // Todos os Espaços pela ordem do seletor; o que está à vista vai sem abas (as dele são as da faixa, gravadas
    // como sempre). null quando só existe o «Geral» intocado: o ficheiro fica como era.
    public (IReadOnlyList<Data.SessionSpace>? Spaces, int Index) WorkspaceSnapshot()
    {
        if (Workspaces.Count == 1 && Workspaces[0].IsPristine) return (null, 0);
        var spaces = Workspaces.Select(w =>
        {
            if (w.IsCurrent) return new Data.SessionSpace(w.Name, w.ColorIndex, w.Icon);
            var tabs = w.StoredTabs;
            var (groupIds, groups) = GroupSnapshotOf(tabs);
            var active = Math.Clamp(w.StoredActiveIndex, 0, Math.Max(0, tabs.Count - 1));
            return new Data.SessionSpace(w.Name, w.ColorIndex, w.Icon, new Data.Session(tabs.Select(t => t.Address).ToList(), active,
                tabs.Select(t => t.Title).ToList(), tabs.Select(t => t.IsPinned).ToList(), groupIds, groups, w.StoredSplit,
                MetaSnapshotOf(tabs, null)));
        }).ToList();
        return (spaces, Workspaces.IndexOf(Current));
    }

    // Depois de Restore (as abas da faixa): recria os Espaços da sessão, com as abas dos outros na reserva, a dormir.
    public void RestoreWorkspaces(IReadOnlyList<Data.SessionSpace>? spaces, int index)
    {
        if (spaces is null || spaces.Count == 0) return;
        index = Math.Clamp(index, 0, spaces.Count - 1);
        var current = Workspaces[0];
        Workspaces.Clear();
        for (var i = 0; i < spaces.Count; i++)
        {
            var space = spaces[i];
            var workspace = i == index ? current : new Workspace();
            workspace.Rename(space.Name, space.ColorIndex, space.Icon);
            if (i != index && space.Tabs is { } saved)
            {
                workspace.StoredTabs.AddRange(BuildTabs(saved.Urls, saved.ActiveIndex, saved.Titles, saved.Pinned, saved.GroupIds, saved.Groups,
                    saved.Metas, out _, wakeActive: false));
                workspace.StoredActiveIndex = saved.ActiveIndex;
                workspace.StoredSplit = saved.Split;
            }
            Workspaces.Add(workspace);
        }
        _workspacesCreated = Workspaces.Count;
        RefreshWorkspaceCounts();
    }
}
