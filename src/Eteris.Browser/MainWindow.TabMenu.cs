using System.Windows;
using System.Windows.Input;
using CefSharp;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

public partial class MainWindow
{
    private void TabItem_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Os eventos Preview descem do pai para o filho: sem isto, o menu da aba ganhava sempre ao do grupo.
        for (var node = e.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, sender);
             node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { Name: "GroupPill" } pill)
            {
                GroupPill_PreviewMouseRightButtonUp(pill, e);
                return;
            }
        }
        if ((sender as FrameworkElement)?.DataContext is not BrowserTab tab) return;
        var selected = SelectedTabs;
        if (TabSelection.OffersBulkActions(selected, tab))
        {
            new DarkMenu().With(menu => AddBulkItems(menu, selected)).Open((UIElement)sender);
            e.Handled = true;
            return;
        }
        var index = Tabs.Tabs.IndexOf(tab);
        var hasRight = Tabs.Tabs.Skip(index + 1).Any(t => !t.IsPinned);
        var hasOthers = Tabs.Tabs.Any(t => !ReferenceEquals(t, tab) && !t.IsPinned);

        new DarkMenu()
            .Item("", "Nova aba à direita", () =>
            {
                Tabs.Active = tab;
                Tabs.NewTab(NewTabUrl, activate: false);
                Tabs.SelectNext();
            })
            .Item("", "Mover para uma nova janela", () => MoveToNewWindow(tab), enabled: Tabs.Tabs.Count > 1)
            .Item("", "Abrir ao lado (ecrã dividido)", () => OpenSideBySide(tab),
                enabled: !ReferenceEquals(tab, Tabs.Active) && Tabs.Active is not null)
            .Separator()
            .With(menu => AddGroupItems(menu, tab))
            .Separator()
            .With(menu => AddWorkspaceItems(menu, tab))
            .Separator()
            .Item("", "Mudar o nome…", () => RenameTab(tab, (UIElement)sender))
            .Item("", tab.HasNote ? "Editar a nota…" : "Escrever nota…", () => RenameTab(tab, (UIElement)sender, focusNote: true))
            .Item(tab.IsLocked ? "" : "", tab.IsLocked ? "Destrancar" : "Trancar", () => tab.IsLocked = !tab.IsLocked)
            .Item("", "Feito", () => MarkTabDone(tab))
            .Item("", "Adiar até…", () => OpenSnoozeMenu(tab, (UIElement)sender), enabled: !IsPrivate && !Browser.NewTabPage.IsNewTab(tab.Address))
            .Item("", tab.IsPinned ? "Desafixar aba" : "Fixar aba", () => Tabs.TogglePin(tab))
            .Item("", "Duplicar", () => Tabs.NewTab(tab.Address))
            .Item(tab.IsMuted ? "" : "", tab.IsMuted ? "Reativar o som" : "Silenciar aba", () => ToggleMute(tab))
            .Item("", "Recarregar", () =>
            {
                Tabs.Active = tab;
                ReloadActive();
            }, "Ctrl+R")
            .Separator()
            .Item("", "Fechar", () => CloseTab(tab), "Ctrl+W")
            .Item("", "Fechar as outras abas", () => Tabs.CloseOthers(tab), enabled: hasOthers)
            .Item("", "Fechar as abas à direita", () => Tabs.CloseToRight(tab), enabled: hasRight)
            .Item("", "Reabrir aba fechada", () => Tabs.ReopenClosed(), "Ctrl+Shift+T")
            .Open((UIElement)sender);
        e.Handled = true;
    }

    // Destaca a aba para uma janela própria (do mesmo tipo: normal ou privada), com o mesmo endereço.
    private void MoveToNewWindow(BrowserTab tab)
    {
        if (Tabs.Tabs.Count < 2) return;
        var address = tab.Address;
        CloseTab(tab);
        OpenWindow(IsPrivate, address);
    }

    private void AddGroupItems(DarkMenu menu, BrowserTab tab)
    {
        if (tab.IsPinned) return;
        if (tab.Group is { } current)
        {
            menu.Item("", $"Remover do grupo «{current.Name}»", () => Tabs.Ungroup(tab));
            return;
        }
        menu.Item("", "Adicionar a um novo grupo", () => StartGroup(tab));
        foreach (var group in Tabs.Groups)
        {
            var target = group;
            menu.Item("", $"Adicionar ao grupo «{target.Name}»", () => Tabs.AddToGroup(tab, target));
        }
    }

    private void StartGroup(BrowserTab tab)
    {
        var group = Tabs.NewGroup(tab);
        ShowToast($"«{group.Name}» criado — clique com o botão direito na etiqueta para mudar o nome ou a cor");
    }
}
