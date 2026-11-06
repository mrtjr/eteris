using System.Windows;
using CefSharp;
using CefSharp.Wpf;
using Eteris.Browser.Browser;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

public partial class MainWindow
{
    private PageMenuHandler NewPageMenuHandler(BrowserTab tab, ChromiumWebBrowser browser)
        => new(IsPrivate, (context, x, y) => Dispatcher.InvokeAsync(() => ShowPageMenu(tab, browser, context, x, y)));

    private void ShowPageMenu(BrowserTab tab, ChromiumWebBrowser browser, PageContext context, int x, int y)
    {
        if (!ReferenceEquals(tab, Tabs.Active)) return;
        var menu = new DarkMenu();
        foreach (var item in PageMenu.Build(context, Engine.Name))
        {
            if (item is null) { menu.Separator(); continue; }
            var command = item.Command;
            menu.Item(item.Glyph, item.Label, () => RunPageCommand(command, tab, browser, context, x, y), item.Gesture, item.Enabled);
        }
        menu.Open(browser);
    }

    private void RunPageCommand(PageCommand command, BrowserTab tab, ChromiumWebBrowser browser, PageContext context, int x, int y)
    {
        var frame = browser.GetBrowser()?.FocusedFrame;
        switch (command)
        {
            case PageCommand.Back: browser.Back(); break;
            case PageCommand.Forward: browser.Forward(); break;
            case PageCommand.Reload: ReloadActive(); break;
            case PageCommand.Print: browser.Print(); break;
            case PageCommand.ViewSource: Tabs.NewTab("view-source:" + tab.Address); break;
            case PageCommand.Inspect: browser.GetBrowserHost()?.ShowDevTools(null, x, y); break;
            case PageCommand.OpenLinkNewTab: Tabs.NewTab(context.LinkUrl!, activate: false); break;
            case PageCommand.OpenLinkPrivate: OpenWindow(isPrivate: true, context.LinkUrl); break;
            case PageCommand.CopyLink: CopyText(context.LinkUrl!, "Endereço do link copiado"); break;
            case PageCommand.OpenImageNewTab: Tabs.NewTab(context.ImageUrl!, activate: false); break;
            case PageCommand.SaveImage: browser.GetBrowserHost()?.StartDownload(context.ImageUrl!); break;
            case PageCommand.CopyImageUrl: CopyText(context.ImageUrl!, "Endereço da imagem copiado"); break;
            case PageCommand.Undo: frame?.Undo(); break;
            case PageCommand.Cut: frame?.Cut(); break;
            case PageCommand.Copy: frame?.Copy(); break;
            case PageCommand.Paste: frame?.Paste(); break;
            case PageCommand.SelectAll: frame?.SelectAll(); break;
            case PageCommand.SearchSelection: Tabs.NewTab(Engine.UrlFor(context.Selection.Trim())); break;
        }
    }

    private void CopyText(string text, string toast)
    {
        try
        {
            Clipboard.SetText(text);
            ShowToast(toast);
        }
        catch (Exception) { }
    }

    // Reaproveita uma aba de histórico já aberta.
    private void OpenHistory()
    {
        if (Tabs.Tabs.FirstOrDefault(t => HistoryPage.IsHistory(t.Address)) is { } open) Tabs.Active = open;
        else Tabs.NewTab(HistoryPage.Url);
    }

    private void ShowDevTools() => ActiveBrowser?.GetBrowserHost()?.ShowDevTools();

    private void ViewSource()
    {
        if (Tabs.Active is { } tab && !tab.Address.StartsWith("view-source:", StringComparison.Ordinal))
            Tabs.NewTab("view-source:" + tab.Address);
    }
}
