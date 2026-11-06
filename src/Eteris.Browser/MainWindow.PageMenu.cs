using System.Windows;
using CefSharp;
using CefSharp.Wpf;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
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
            case PageCommand.CopyLink: CopyText(Shields.UrlCleaner.Clean(context.LinkUrl!), "Endereço do link copiado (sem rastreio)"); break;
            case PageCommand.OpenImageNewTab: Tabs.NewTab(context.ImageUrl!, activate: false); break;
            case PageCommand.SaveImage: browser.GetBrowserHost()?.StartDownload(context.ImageUrl!); break;
            case PageCommand.CopyImageUrl: CopyText(context.ImageUrl!, "Endereço da imagem copiado"); break;
            case PageCommand.Undo: frame?.Undo(); break;
            case PageCommand.Cut: frame?.Cut(); break;
            case PageCommand.Copy: frame?.Copy(); break;
            case PageCommand.Paste: frame?.Paste(); break;
            case PageCommand.SelectAll: frame?.SelectAll(); break;
            case PageCommand.HideElement: PickElementToHide(); break;
            case PageCommand.SavePdf: SaveAsPdf(); break;
            case PageCommand.ReadLater: SaveForLater(); break;
            case PageCommand.SearchSelection: Tabs.NewTab(Engine.UrlFor(context.Selection.Trim())); break;
            case PageCommand.CopyHighlightLink:
                if (TextFragment.Link(tab.Address, context.Selection) is { } highlight)
                    CopyText(highlight, "Link para o destaque copiado — abre a página já no texto selecionado");
                break;
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

    private void SaveForLater()
    {
        if (Tabs.Active is not { } tab || WebHost(tab.Address) is null) return;
        AppData.SaveForLater(tab.Address, tab.Title);
        ShowToast("Guardado em «Para ler» — veja em Favoritos › Para ler");
    }

    // Páginas internas: reaproveita uma aba já aberta em vez de duplicar.
    private void OpenInternal(string url)
    {
        if (Tabs.Tabs.FirstOrDefault(t => t.Address.StartsWith(url, StringComparison.OrdinalIgnoreCase)) is { } open) Tabs.Active = open;
        else Tabs.NewTab(url);
    }

    private void OpenHistory() => OpenInternal(HistoryPage.Url);

    private void OpenSettings() => OpenInternal(SettingsPage.Url);

    private void OpenBookmarks() => OpenInternal(BookmarksPage.Url);

    private void ShowDevTools() => ActiveBrowser?.GetBrowserHost()?.ShowDevTools();

    private void ViewSource()
    {
        if (Tabs.Active is { } tab && !tab.Address.StartsWith("view-source:", StringComparison.Ordinal))
            Tabs.NewTab("view-source:" + tab.Address);
    }
}
