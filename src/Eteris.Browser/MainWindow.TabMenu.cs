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
        if ((sender as FrameworkElement)?.DataContext is not BrowserTab tab) return;
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
            .Separator()
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
}
