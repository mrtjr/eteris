using System.Collections.ObjectModel;

namespace Eteris.Browser.Tabs;

// Espaços (Tabs/Workspace.cs): a faixa (Tabs) mostra só as abas do Espaço à vista. Mudar de Espaço guarda as abas
// atuais na reserva do Espaço (a dormir, com a aba ativa e a ordem) e põe na faixa as do destino; a aba ativa do
// destino acorda pela janela (OnActiveTabChanging). As abas entram na faixa antes de as antigas saírem, para a
// janela nunca ficar sem abas (a última aba a sair fechá-la-ia). As abas que saem adormecem, menos as trancadas e as
// que tocam som: essas guardam o motor fora de vista (a janela não o liberta) e voltam como estavam.
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
        var now = UtcNow();
        var leaving = Tabs.ToList();
        current.StoredTabs.Clear();
        current.StoredTabs.AddRange(leaving);
        var leavingActive = Active;
        current.StoredActiveIndex = leavingActive is not null ? Math.Max(0, leaving.IndexOf(leavingActive)) : 0;
        current.IsCurrent = false;
        current.LeftUtc = now;
        foreach (var tab in leaving)
        {
            tab.IsMultiSelected = false;
            // Ficam acordadas as que a janela segura (KeepsAwake: captura, multimédia a tocar, a página que pediu para
            // ficar) e a aba em uso (decisão D3: pode ter texto por enviar; o sono por tempo pergunta à página antes).
            if (!StaysAwakeOutOfView(tab) && !(!tab.IsSleeping && (KeepsAwake?.Invoke(tab) == true || ReferenceEquals(tab, leavingActive))))
                tab.IsSleeping = true;
        }

        // 2. As abas do destino entram (a dormir) e a sua ativa acorda; um Espaço vazio ganha uma aba nova.
        var arriving = target.StoredTabs.ToList();
        var activeIndex = Math.Clamp(target.StoredActiveIndex, 0, Math.Max(0, arriving.Count - 1));
        target.StoredTabs.Clear();
        target.IsCurrent = true;
        // O tempo em que o Espaço esteve posto de lado não conta para o arquivo automático: voltar a um Espaço
        // guardado há mais do que o prazo não pode mandar as abas dele para o Arquivo no minuto seguinte.
        if (target.LeftUtc is { } left && now > left)
            foreach (var tab in arriving) tab.LastActiveUtc = Min(now, tab.LastActiveUtc + (now - left));
        target.LeftUtc = null;
        // A ativa entra já acordada (a janela cria-lhe o motor ao entrar na faixa, como no restauro da sessão).
        if (arriving.Count > 0) arriving[activeIndex].IsSleeping = false;
        foreach (var tab in arriving) Tabs.Add(tab);
        if (arriving.Count > 0) Active = arriving[activeIndex];
        else NewTab(newTabAddress ?? NewTabAddress);

        // 3. Só agora as abas do Espaço que saiu deixam a faixa (a janela liberta os motores delas).
        foreach (var tab in leaving) Tabs.Remove(tab);
        RefreshGroups();
        RefreshWorkspaceCounts();
        return true;
    }

    private static DateTime Min(DateTime a, DateTime b) => a <= b ? a : b;

    // O que a janela sabe e o modelo não (MainWindow.xaml.cs): uma aba que usa a câmara, o microfone ou o ecrã, que
    // toca multimédia ou cuja página pediu para ficar acordada não adormece ao mudar de Espaço.
    public Func<BrowserTab, bool>? KeepsAwake { get; set; }

    // Uma aba acordada que tranca ou toca som não adormece ao sair de vista (como na política de sono).
    public static bool StaysAwakeOutOfView(BrowserTab tab) => !tab.IsSleeping && (tab.IsLocked || tab.IsAudible);

    // As abas acordadas fora de vista (reserva de outros Espaços), para a janela as vigiar e adormecer pela política.
    public IReadOnlyList<BrowserTab> AwakeOutOfView()
        => Workspaces.Where(w => !w.IsCurrent).SelectMany(w => w.StoredTabs).Where(t => !t.IsSleeping).ToList();

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
        if (Tabs.Count == 1) NewTab(newTabAddress ?? NewTabAddress, activate: false);
        var index = Tabs.IndexOf(tab);
        var wasActive = ReferenceEquals(tab, Active);
        tab.Group = null;
        tab.IsMultiSelected = false;
        tab.IsSleeping = true;
        if (wasActive) Active = SuccessorOf(tab) ?? Tabs[index == Tabs.Count - 1 ? index - 1 : index + 1];
        Tabs.RemoveAt(index);
        RefreshGroups();

        var stored = target.StoredTabs;
        var at = tab.IsPinned ? stored.Count(t => t.IsPinned) : stored.Count;
        stored.Insert(at, tab);
        ShiftStoredIndices(target, at);
        RefreshWorkspaceCounts();
        return true;
    }

    // Uma aba entrou na reserva na posição «at» (uma fixada vai para junto das fixadas, antes das outras): a aba
    // ativa e o ecrã dividido guardados por índice continuam a apontar para as mesmas abas.
    private static void ShiftStoredIndices(Workspace target, int at)
    {
        if (at >= target.StoredTabs.Count - 1) return;
        if (target.StoredActiveIndex >= at) target.StoredActiveIndex++;
        if (target.StoredSplit is { } split)
            target.StoredSplit = split with
            {
                Left = split.Left >= at ? split.Left + 1 : split.Left,
                Right = split.Right >= at ? split.Right + 1 : split.Right,
            };
    }

    public bool RemoveWorkspace(Workspace workspace) => RemoveWorkspaceWithTabs(workspace) is not null;

    // Apagar um Espaço fecha as abas dele (vão para as fechadas recentes); o último não se apaga. Apagar o Espaço à
    // vista muda primeiro para o vizinho. As trancadas não fecham: passam para o fim da faixa do Espaço que fica à
    // vista. Devolve as abas fechadas (para a janela libertar motores e guardar o que não cabe nas fechadas
    // recentes) e quantas trancadas ficaram; null se não apagou.
    public WorkspaceRemoval? RemoveWorkspaceWithTabs(Workspace workspace)
    {
        if (Workspaces.Count < 2 || !Workspaces.Contains(workspace)) return null;
        if (workspace.IsCurrent)
        {
            var index = Workspaces.IndexOf(workspace);
            SwitchWorkspace(Workspaces[index > 0 ? index - 1 : index + 1]);
        }
        var locked = workspace.StoredTabs.Where(t => t.IsLocked).ToList();
        var closed = workspace.StoredTabs.Where(t => !t.IsLocked).ToList();
        foreach (var tab in closed)
        {
            if (Browser.NewTabPage.IsNewTab(tab.Address)) continue;
            _closed.Add(new ClosedTab(Tabs.Count, tab.Address, tab.Title));
            if (_closed.Count > MaxClosed) _closed.RemoveAt(0);
        }
        workspace.StoredTabs.Clear();
        Workspaces.Remove(workspace);
        foreach (var tab in locked)
        {
            tab.Group = null;
            Tabs.Add(tab);
        }
        RefreshGroups();
        RefreshWorkspaceCounts();
        return new WorkspaceRemoval(closed, locked.Count);
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
                MetaSnapshotOf(tabs, null)), w.LeftUtc is { } left ? UnixMs(left) : null);
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
                // Desde quando está posto de lado; numa sessão antiga, a última visita a uma aba dele.
                workspace.LeftUtc = space.Left is { } leftMs && leftMs is >= 0 and <= MaxUnixMilliseconds
                    ? DateTimeOffset.FromUnixTimeMilliseconds(leftMs).UtcDateTime
                    : workspace.StoredTabs.Count > 0 ? workspace.StoredTabs.Max(t => t.LastActiveUtc) : null;

            }
            Workspaces.Add(workspace);
        }
        _workspacesCreated = Workspaces.Count;
        RefreshWorkspaceCounts();
    }
}

// O resultado de apagar um Espaço: as abas que fecharam e quantas trancadas passaram para o Espaço à vista.
public sealed record WorkspaceRemoval(IReadOnlyList<BrowserTab> Closed, int KeptLocked);
