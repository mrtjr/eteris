using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
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
        Tabs.Workspaces.CollectionChanged += (_, _) =>
        {
            UpdateSpaceStrip();
            ScheduleSessionSave();
        };
        UpdateSpaceStrip();
    }

    // Os chips só aparecem a partir do segundo Espaço: com um só, a barra fica como sempre foi.
    private void UpdateSpaceStrip()
    {
        SpaceStrip.Visibility = Tabs.HasWorkspaces ? Visibility.Visible : Visibility.Collapsed;
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

    private FrameworkElement? ChipOf(Workspace workspace)
        => SpaceStrip.Visibility == Visibility.Visible ? SpaceStrip.ItemContainerGenerator.ContainerFromItem(workspace) as FrameworkElement : null;

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

    // Apagar fecha as abas do Espaço (ficam nas fechadas recentes); o atual passa primeiro para o vizinho.
    private void DeleteWorkspace(Workspace workspace)
    {
        if (!Tabs.HasWorkspaces) return;
        var count = workspace.TabCount;
        if (workspace.IsCurrent)
        {
            var index = Tabs.Workspaces.IndexOf(workspace);
            SwitchWorkspace(Tabs.Workspaces[index > 0 ? index - 1 : index + 1]);
        }
        if (!Tabs.RemoveWorkspace(workspace)) return;
        ShowToast(count == 0
            ? $"Espaço «{workspace.Name}» apagado"
            : $"Espaço «{workspace.Name}» apagado — {Workspace.CountText(count)} nas fechadas recentes (Ctrl+Shift+T)");
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
            var keys = i < 9 ? $"Ctrl+Alt+{i + 1}" : null;
            yield return new("", $"Ir para o Espaço «{target.Name}»", $"{(target.Icon is { } icon ? icon + " · " : "")}{Workspace.CountText(target.TabCount)}",
                keys, () => SwitchWorkspace(target, announce: true));
        }
        yield return new("", "Espaço seguinte", "Dá a volta pelos Espaços desta janela", "Ctrl+Alt+→", () => RunWorkspaceShortcut(WorkspaceMove.Next));
        yield return new("", "Espaço anterior", "Dá a volta pelos Espaços desta janela", "Ctrl+Alt+←", () => RunWorkspaceShortcut(WorkspaceMove.Previous));
        var current = Tabs.Current;
        yield return new("", "Mudar o nome do Espaço…", $"Agora «{current.Name}» — nome, emoji e cor do Espaço à vista", null,
            () => EditWorkspace(current, ChipOf(current) ?? Omnibox));
        yield return new("", $"Apagar o Espaço «{current.Name}»", "As abas dele vão para as fechadas recentes", null, () => DeleteWorkspace(current));
    }
}
