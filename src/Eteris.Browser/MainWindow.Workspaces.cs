using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Palette;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// Espaços na janela: o seletor de chips na barra de título (um por Espaço, com a cor e o emoji ou a inicial; o
// atual com o nome), a mudança de Espaço com o ecrã dividido guardado e reposto, os atalhos Ctrl+Alt+1…9 e
// Ctrl+Alt+←/→ e os comandos da paleta. O modelo e as regras estão em Tabs/Workspace.cs e
// Tabs/TabsViewModel.Workspaces.cs; as abas dos Espaços fora de vista ficam a dormir (sem motor).
public partial class MainWindow
{
    private void WatchWorkspaces()
    {
        foreach (var workspace in Tabs.Workspaces) workspace.PropertyChanged += OnWorkspacePropertyChanged;
        Tabs.Workspaces.CollectionChanged += (_, e) =>
        {
            foreach (Workspace workspace in e.OldItems ?? Array.Empty<Workspace>()) workspace.PropertyChanged -= OnWorkspacePropertyChanged;
            foreach (Workspace workspace in e.NewItems ?? Array.Empty<Workspace>()) workspace.PropertyChanged += OnWorkspacePropertyChanged;
            UpdateSpaceStrip();
            ApplyTint();
            ScheduleSessionSave();
            if (!IsPrivate) Sync.SyncService.Touch();
        };
        UpdateSpaceStrip();
    }

    // Nome, cor e emoji vão para a sessão; a cor do Espaço à vista tinge a barra na hora.
    private void OnWorkspacePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Workspace.Name) or nameof(Workspace.ColorIndex) or nameof(Workspace.Icon))
        {
            ScheduleSessionSave();
            if (!IsPrivate) Sync.SyncService.Touch();
        }
        if (e.PropertyName == nameof(Workspace.ColorIndex) && sender is Workspace { IsCurrent: true }) ApplyTint();
    }

    // Os chips só aparecem a partir do segundo Espaço: com um só, a barra fica como sempre foi.
    private void UpdateSpaceStrip()
    {
        SpaceStrip.Visibility = SideSpaceStrip.Visibility = Tabs.HasWorkspaces ? Visibility.Visible : Visibility.Collapsed;
        UpdateTabWidth();
    }

    private void SpaceStrip_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTabWidth();

    // Muda de Espaço: a divisão do ecrã atual fica guardada no Espaço que sai e a do destino é reposta.
    private void SwitchWorkspace(Workspace target, bool announce = false)
    {
        if (target.IsCurrent || !Tabs.Workspaces.Contains(target)) return;
        CloseSuggestions();
        CloseFind();
        CloseRename();
        HideGroupPreview();
        ClearSelection();
        Tabs.Current.StoredSplit = SplitSnapshot();
        Tabs.SwitchWorkspace(target, NewTabUrl);
        RestoreSplit(target.StoredSplit);
        target.StoredSplit = null;
        UpdateTabWidth();
        RevealActiveTab();
        ApplyTint();
        ScheduleSessionSave();
        if (announce) ShowToast($"{(target.Icon is { } icon ? icon + " " : "")}Espaço «{target.Name}» · {Workspace.CountText(target.TabCount)}");
    }

    // Cria, muda para ele e abre logo o editor (nome, emoji, cor) por baixo do chip novo.
    private void NewWorkspaceFromUi()
    {
        var workspace = Tabs.NewWorkspace(activate: false);
        SwitchWorkspace(workspace);
        Dispatcher.InvokeAsync(() => EditWorkspace(workspace, ChipOf(workspace) ?? Omnibox, isNew: true), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // O chip do Espaço na barra que está à vista (título, ou lateral nas abas verticais); null com um só Espaço.
    private FrameworkElement? ChipOf(Workspace workspace)
    {
        if (!Tabs.HasWorkspaces) return null;
        var strip = SideBar.Visibility == Visibility.Visible ? SideSpaceStrip : SpaceStrip;
        return strip.ItemContainerGenerator.ContainerFromItem(workspace) as FrameworkElement;
    }

    // O Espaço cujo chip está debaixo do rato durante um arrasto de aba (nunca o atual: a aba já lá está).
    private Workspace? WorkspaceUnder(MouseEventArgs e)
    {
        if (SpaceStrip.Visibility != Visibility.Visible) return null;
        foreach (var workspace in Tabs.Workspaces)
        {
            if (workspace.IsCurrent || ChipOf(workspace) is not { } chip) continue;
            var position = e.GetPosition(chip);
            if (position.X >= 0 && position.Y >= 0 && position.X < chip.ActualWidth && position.Y < chip.ActualHeight) return workspace;
        }
        return null;
    }

    private void ShowWorkspaceDropHint(Workspace? target)
    {
        foreach (var workspace in Tabs.Workspaces) workspace.IsDropTarget = ReferenceEquals(workspace, target);
    }

    // «Mover para o Espaço…» no menu da aba (e das abas selecionadas): um item por Espaço e «um novo Espaço».
    private void AddWorkspaceItems(DarkMenu menu, BrowserTab tab) => AddWorkspaceItems(menu, [tab]);

    private void AddWorkspaceItems(DarkMenu menu, IReadOnlyList<BrowserTab> tabs)
    {
        var what = tabs.Count == 1 ? "Mover" : $"Mover {TabSelection.Count(tabs.Count)}";
        foreach (var workspace in Tabs.Workspaces.Where(w => !w.IsCurrent))
        {
            var target = workspace;
            menu.Item("", $"{what} para o Espaço «{target.Name}»", () => MoveToWorkspace(tabs, target));
        }
        menu.Item("", $"{what} para um novo Espaço", () => MoveToWorkspace(tabs, null));
    }

    // As abas saem a dormir para a reserva do destino (um Espaço novo, se «target» for null) e a janela fica aqui.
    private void MoveToWorkspace(IReadOnlyList<BrowserTab> tabs, Workspace? target)
    {
        target ??= Tabs.NewWorkspace(activate: false);
        var moved = tabs.Count(tab => Tabs.MoveToWorkspace(tab, target, NewTabUrl));
        ClearSelection();
        ScheduleSessionSave();
        if (moved == 0) return;
        ShowToast(moved == 1
            ? $"«{tabs[0].DisplayTitle}» foi para o Espaço «{target.Name}»"
            : $"{TabSelection.Count(moved)} foram para o Espaço «{target.Name}»");
    }

    // Apagar fecha as abas do Espaço (ficam nas fechadas recentes); o atual passa primeiro para o vizinho. As trancadas
    // não fecham: passam para o Espaço à vista. Mais abas do que cabem nas fechadas recentes ficam também numa sessão
    // guardada (janela normal), com nome e nota, para nenhuma se perder.
    private void DeleteWorkspace(Workspace workspace)
    {
        if (!Tabs.HasWorkspaces) return;
        if (workspace.IsCurrent)
        {
            var index = Tabs.Workspaces.IndexOf(workspace);
            SwitchWorkspace(Tabs.Workspaces[index > 0 ? index - 1 : index + 1]);
        }
        var name = workspace.Name;
        var pages = workspace.TabCount > 0 ? Tabs.TabsOf(workspace).Where(t => !t.IsLocked && !NewTabPage.IsNewTab(t.Address))
            .Select(t => new SavedTab(t.Address, t.Title, t.CustomTitle, t.Note)).ToList() : [];
        if (Tabs.RemoveWorkspaceWithTabs(workspace) is not { } removal) return;
        // Abas acordadas na reserva (com som) ainda tinham motor.
        foreach (var tab in removal.Closed) ReleaseBrowser(tab);
        // As abas da reserva saem sem passar pela faixa: «Esquecer ao fechar» também as conta.
        if (!IsPrivate) ScheduleForgetFor(removal.Closed.Select(t => t.Address));
        var kept = removal.KeptLocked == 0 ? ""
            : removal.KeptLocked == 1 ? " — a aba trancada passou para este Espaço" : $" — as {removal.KeptLocked} abas trancadas passaram para este Espaço";
        if (pages.Count == 0)
        {
            ShowToast($"Espaço «{name}» apagado{kept}");
            return;
        }
        if (pages.Count > TabsViewModel.MaxClosed && !IsPrivate && AppData.SaveSession($"Espaço «{name}» (apagado)", pages))
        {
            ShowToast($"Espaço «{name}» apagado — {Workspace.CountText(pages.Count)} na sessão guardada «Espaço «{name}» (apagado)» (paleta, Ctrl+K){kept}");
            return;
        }
        ShowToast(pages.Count > TabsViewModel.MaxClosed
            ? $"Espaço «{name}» apagado — as últimas {TabsViewModel.MaxClosed} de {pages.Count} abas nas fechadas recentes (Ctrl+Shift+T){kept}"
            : $"Espaço «{name}» apagado — {Workspace.CountText(pages.Count)} nas fechadas recentes (Ctrl+Shift+T){kept}");
    }

    private void SpaceChip_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Workspace workspace) return;
        e.Handled = true;
        SwitchWorkspace(workspace);
        ReturnFocusToPage();
    }

    private void SpaceChip_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Workspace workspace) return;
        e.Handled = true;
        new DarkMenu()
            .Item("", "Ir para este Espaço", () => SwitchWorkspace(workspace), enabled: !workspace.IsCurrent)
            .Item("", "Mudar o nome, o emoji ou a cor…", () => EditWorkspace(workspace, (UIElement)sender))
            .Item("", "Novo Espaço", NewWorkspaceFromUi)
            .Separator()
            .Item("", $"Apagar «{workspace.Name}»", () => DeleteWorkspace(workspace), enabled: Tabs.HasWorkspaces)
            .OpenBelow((UIElement)sender);
    }

    // ---------- Atalhos ----------

    private void RunWorkspaceShortcut(WorkspaceMove move)
    {
        if (!Tabs.HasWorkspaces)
        {
            if (move.Offset != 0 || move.Number > 1) ShowToast("Só há um Espaço — «Novo Espaço» na paleta (Ctrl+K)");
            return;
        }
        var spaces = Tabs.Workspaces;
        var index = move.Offset != 0
            ? (spaces.IndexOf(Tabs.Current) + move.Offset + spaces.Count) % spaces.Count
            : Workspace.IndexForNumber(move.Number, spaces.Count);
        if (index >= 0) SwitchWorkspace(spaces[index], announce: true);
    }

    // O Alt da esquerda físico: o AltGr (Alt da direita + Ctrl) nunca conta como Ctrl+Alt. Lido pelo sistema, por
    // isso serve tanto na thread da janela como na do CEF (ShortcutFromPage).
    private static bool IsLeftAltDown()
        => (GetAsyncKeyState(VkLeftMenu) & 0x8000) != 0 && (GetAsyncKeyState(VkRightMenu) & 0x8000) == 0;

    private const int VkLeftMenu = 0xA4;
    private const int VkRightMenu = 0xA5;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    // ---------- Paleta ----------

    private IEnumerable<PaletteCommand> WorkspaceCommands()
    {
        yield return new("", "Novo Espaço", "Outro ambiente de abas nesta janela, com nome, cor e ícone próprios", null, NewWorkspaceFromUi);
        if (!Tabs.HasWorkspaces) yield break;
        var spaces = Tabs.Workspaces;
        for (var i = 0; i < spaces.Count; i++)
        {
            var target = spaces[i];
            if (target.IsCurrent) continue;
            var keys = Workspace.ShortcutFor(i, spaces.Count);
            yield return new("", $"Ir para o Espaço «{target.Name}»", $"{(target.Icon is { } icon ? icon + " · " : "")}{Workspace.CountText(target.TabCount)}",
                keys, () => SwitchWorkspace(target, announce: true));
        }
        yield return new("", "Espaço seguinte", "Dá a volta pelos Espaços desta janela", "Ctrl+Alt+→", () => RunWorkspaceShortcut(WorkspaceMove.Next));
        yield return new("", "Espaço anterior", "Dá a volta pelos Espaços desta janela", "Ctrl+Alt+←", () => RunWorkspaceShortcut(WorkspaceMove.Previous));
        var current = Tabs.Current;
        yield return new("", "Mudar o nome do Espaço…", $"Agora «{current.Name}» — nome, emoji e cor do Espaço à vista", null,
            () => EditWorkspace(current, ChipOf(current) ?? Omnibox));
        yield return new("", $"Apagar o Espaço «{current.Name}»", "As abas dele vão para as fechadas recentes; as trancadas passam para o vizinho", null,
            () => DeleteWorkspace(current));
    }
}
