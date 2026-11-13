using CefSharp;

namespace Eteris.Browser.Browser;

// Os tratadores mudos dos motores escondidos (o Espreitar, MainWindow.Glance.cs, e o motor da vigia, Watching/
// WatchRunner.cs): nunca roubam o teclado, nunca transferem, nunca mostram menus, diálogos nem pedidos de permissão.

// Nunca rouba o teclado à página que o utilizador está a ver.
internal sealed class SilentFocusHandler : IFocusHandler
{
    public void OnGotFocus(IWebBrowser chromiumWebBrowser, IBrowser browser) { }
    public bool OnSetFocus(IWebBrowser chromiumWebBrowser, IBrowser browser, CefFocusSource source) => true;
    public void OnTakeFocus(IWebBrowser chromiumWebBrowser, IBrowser browser, bool next) { }
}

// O recorte ao vivo (ClipWindow) é interativo: o utilizador clica e escreve nele, e esse foco (o clique, que o Windows dá
// à janela nativa) vale. Mas uma carga — a primeira, «Recarregar», a recarga marcada de 5 em 5 min, um autofocus da página
// — pede o foco com FocusSourceNavigation, e a janela do recorte, sempre por cima, tirava o teclado à aba ou à barra de
// endereço onde o utilizador estava a escrever. Esse é recusado.
internal sealed class ClipFocusHandler : IFocusHandler
{
    public static bool Cancels(CefFocusSource source) => source == CefFocusSource.FocusSourceNavigation;

    public void OnGotFocus(IWebBrowser chromiumWebBrowser, IBrowser browser) { }
    public bool OnSetFocus(IWebBrowser chromiumWebBrowser, IBrowser browser, CefFocusSource source) => Cancels(source);
    public void OnTakeFocus(IWebBrowser chromiumWebBrowser, IBrowser browser, bool next) { }
}

// Transferências recusadas; «onRefused» diz a quem criou o motor que houve uma (na thread do CEF).
internal sealed class SilentDownloadHandler(Action onRefused) : CefSharp.Handler.DownloadHandler
{
    protected override bool CanDownload(IWebBrowser chromiumWebBrowser, IBrowser browser, string url, string requestMethod)
    {
        onRefused();
        return false;
    }
}

internal sealed class SilentMenuHandler : IContextMenuHandler
{
    public void OnBeforeContextMenu(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IContextMenuParams parameters, IMenuModel model)
        => model.Clear();
    public bool OnContextMenuCommand(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IContextMenuParams parameters,
        CefMenuCommand commandId, CefEventFlags eventFlags) => false;
    public void OnContextMenuDismissed(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame) { }
    public bool RunContextMenu(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IContextMenuParams parameters, IMenuModel model,
        IRunContextMenuCallback callback)
    {
        callback.Cancel();
        return true;
    }
}

// Nenhum pedido de permissão chega ao utilizador nem fica decidido: a câmara, o microfone e o ecrã são cancelados, e os
// outros pedidos são descartados («Dismiss»), nunca recusados — uma recusa ficava gravada para sempre nas definições do
// motor (ver SitePermissionHandler), e o motor da vigia pode correr no contexto do perfil («com a minha sessão»).
internal sealed class SilentPermissionHandler : CefSharp.Handler.PermissionHandler
{
    protected override bool OnRequestMediaAccessPermission(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame,
        string requestingOrigin, MediaAccessPermissionType requestedPermissions, IMediaAccessCallback callback)
    {
        using (callback) callback.Cancel();
        return true;
    }

    protected override bool OnShowPermissionPrompt(IWebBrowser chromiumWebBrowser, IBrowser browser, ulong promptId,
        string requestingOrigin, PermissionRequestType permissionRequestTypes, IPermissionPromptCallback callback)
    {
        using (callback) callback.Continue(PermissionRequestResult.Dismiss);
        return true;
    }
}

// Os diálogos de JavaScript (alert, confirm, prompt) respondem «cancelar» sozinhos, sem janela; o «sair desta página?»
// deixa sair (uma página não prende o motor).
internal sealed class SilentDialogHandler : IJsDialogHandler
{
    public bool OnJSDialog(IWebBrowser chromiumWebBrowser, IBrowser browser, string originUrl, CefJsDialogType dialogType, string messageText,
        string defaultPromptText, IJsDialogCallback callback, ref bool suppressMessage)
    {
        using (callback) callback.Continue(false);
        return true;
    }

    public bool OnBeforeUnloadDialog(IWebBrowser chromiumWebBrowser, IBrowser browser, string messageText, bool isReload, IJsDialogCallback callback)
    {
        using (callback) callback.Continue(true);
        return true;
    }

    public void OnResetDialogState(IWebBrowser chromiumWebBrowser, IBrowser browser) { }

    public void OnDialogClosed(IWebBrowser chromiumWebBrowser, IBrowser browser) { }
}
