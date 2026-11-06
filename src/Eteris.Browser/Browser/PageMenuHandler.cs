using CefSharp;

namespace Eteris.Browser.Browser;

// Substitui o menu nativo do CEF (claro, em inglês) pelo menu escuro do Éteris.
// Lê o contexto no thread do CEF, esvazia o modelo (o CEF não mostra nada) e entrega o resto à janela.
public sealed class PageMenuHandler(bool isPrivate, Action<PageContext, int, int> show) : IContextMenuHandler
{
    public void OnBeforeContextMenu(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        IContextMenuParams parameters, IMenuModel model)
    {
        var context = new PageContext(
            LinkUrl: parameters.LinkUrl,
            ImageUrl: parameters.MediaType == ContextMenuMediaType.Image && parameters.HasImageContents ? parameters.SourceUrl : null,
            Selection: parameters.SelectionText ?? string.Empty,
            IsEditable: parameters.IsEditable,
            CanGoBack: browser.CanGoBack,
            CanGoForward: browser.CanGoForward,
            IsPrivate: isPrivate,
            IsWebPage: frame.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase));
        var (x, y) = (parameters.XCoord, parameters.YCoord);
        model.Clear();
        show(context, x, y);
    }

    public bool OnContextMenuCommand(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        IContextMenuParams parameters, CefMenuCommand commandId, CefEventFlags eventFlags) => false;

    public void OnContextMenuDismissed(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame) { }

    public bool RunContextMenu(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        IContextMenuParams parameters, IMenuModel model, IRunContextMenuCallback callback)
    {
        callback.Cancel();
        return true;
    }
}
