using System.Windows;
using CefSharp;
using CefSharp.Wpf.HwndHost;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

public partial class MainWindow
{
    private PageMenuHandler NewPageMenuHandler(BrowserTab tab, ChromiumWebBrowser browser)
        => new(IsPrivate, (context, x, y) => Dispatcher.InvokeAsync(() => ShowPageMenu(tab, browser, context, x, y)));

    private async void ShowPageMenu(BrowserTab tab, ChromiumWebBrowser browser, PageContext context, int x, int y)
    {
        if (!ReferenceEquals(tab, Tabs.Active)) return;
        // Num campo de uma página http(s): o que o script do cofre viu nele (MainWindow.Vault.cs).
        context = await WithVaultField(tab, context);
        if (!ReferenceEquals(tab, Tabs.Active)) return;
        // O Avançar também volta ao vídeo de onde se regressou com o Voltar (Tabs/EngineCrossing.cs).
        context = context with { CanGoForward = tab.Crossing.CanGoForward(inVideo: false, context.CanGoForward) };
        var menu = new DarkMenu();
        var vaultField = context.Vault is null ? null : _vaultContextField;
        foreach (var item in PageMenu.Build(context, Engine.Name))
        {
            if (item is null) { menu.Separator(); continue; }
            if (item.Command == PageCommand.FillWith && vaultField is not null)
            {
                menu.Submenu(item.Glyph, item.Label, sub => AddFillWithMenu(sub, vaultField));
                continue;
            }
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
            case PageCommand.Forward when !browser.CanGoForward && ReferenceEquals(ActiveBrowser, browser): GoForward(); break;
            case PageCommand.Forward: browser.Forward(); break;
            case PageCommand.Reload: ReloadActive(); break;
            case PageCommand.Print: browser.Print(); break;
            case PageCommand.ViewSource: Tabs.NewTab("view-source:" + tab.Address); break;
            case PageCommand.Inspect: browser.GetBrowserHost()?.ShowDevTools(null, x, y); break;
            case PageCommand.OpenLinkNewTab when PageLinks.CanOpenLink(context.LinkUrl, context.PageUrl): Tabs.NewTab(context.LinkUrl!, activate: false); break;
            case PageCommand.PreviewLink: ShowLinkPreview(context.LinkUrl!); break;
            case PageCommand.OpenLinkSideBySide when PageLinks.CanOpenLink(context.LinkUrl, context.PageUrl): OpenSideBySide(Tabs.NewTab(context.LinkUrl!, activate: false)); break;
            case PageCommand.OpenLinkPrivate when PageLinks.CanOpenLink(context.LinkUrl, context.PageUrl): OpenWindow(isPrivate: true, context.LinkUrl); break;
            case PageCommand.CopyLink: CopyText(Shields.UrlCleaner.Clean(context.LinkUrl!), "Endereço do link copiado (sem rastreio)"); break;
            case PageCommand.OpenImageNewTab when PageLinks.CanOpenImage(context.ImageUrl): Tabs.NewTab(context.ImageUrl!, activate: false); break;
            case PageCommand.SaveImage:
                DownloadHandler.UserRequested(context.ImageUrl!);
                browser.GetBrowserHost()?.StartDownload(context.ImageUrl!);
                break;
            case PageCommand.CopyImageUrl: CopyText(context.ImageUrl!, "Endereço da imagem copiado"); break;
            case PageCommand.Undo: frame?.Undo(); break;
            case PageCommand.Cut: frame?.Cut(); break;
            case PageCommand.Copy: frame?.Copy(); break;
            case PageCommand.Paste: frame?.Paste(); break;
            case PageCommand.SelectAll: frame?.SelectAll(); break;
            case PageCommand.HideElement: PickElementToHide(); break;
            case PageCommand.SavePdf: SaveAsPdf(); break;
            case PageCommand.ReadLater: SaveForLater(); break;
            case PageCommand.PlayInVideoEngine: PlayInVideoEngine(tab); break;
            case PageCommand.GeneratePassword: _ = GenerateFromMenuAsync(tab); break;
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
        if (IsPrivate) return;
        if (Tabs.Active is not { } tab || WebHost(tab.Address) is null) return;
        AppData.SaveForLater(tab.Address, tab.Title);
        ShowToast("Guardado em «Para ler» — veja em Favoritos › Para ler");
    }

    // Páginas internas: reaproveita uma aba já aberta em vez de duplicar. Com uma âncora (os botões das barras que
    // levam a um item das Definições), a aba da mesma página serve e vai até ao item: outro endereço carrega-se (o hash
    // novo leva a vista e abre «Mais opções»); o mesmo endereço carrega outra vez (o mesmo hash não voltava a levar a
    // vista ao item, que podia estar em «Mais opções» já fechado).
    private void OpenInternal(string url)
    {
        var hash = url.IndexOf('#');
        if (hash < 0)
        {
            if (Tabs.Tabs.FirstOrDefault(t => t.Address.StartsWith(url, StringComparison.OrdinalIgnoreCase)) is { } same) Tabs.Active = same;
            else Tabs.NewTab(url);
            return;
        }
        var page = url[..hash];
        if (Tabs.Tabs.FirstOrDefault(t => t.Address.StartsWith(page, StringComparison.OrdinalIgnoreCase)) is not { } open)
        {
            Tabs.NewTab(url);
            return;
        }
        Tabs.Active = open;
        if (string.Equals(open.Address, url, StringComparison.OrdinalIgnoreCase)) ReloadActive();
        else LoadUrl(url);
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
