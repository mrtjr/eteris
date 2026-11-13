using CefSharp;

namespace Eteris.Browser.Browser;

// Os diálogos de JavaScript das páginas de uma janela privada (MainWindow.PrivateLock.cs). Com a janela trancada, o alert
// não aparece e o confirm e o prompt contam como cancelados (um diálogo do Chromium é uma janela por cima do cartão, com o
// texto da página); o «sair desta página?» deixa sair, para fechar uma janela trancada nunca ficar preso numa pergunta do
// site. Destrancada, o motor faz o que fazia (false em tudo). locked é lido no fio do CEF.
internal sealed class PrivateLockDialogHandler(Func<bool> locked) : IJsDialogHandler
{
    private readonly Func<bool> _locked = locked;

    public bool OnJSDialog(IWebBrowser chromiumWebBrowser, IBrowser browser, string originUrl, CefJsDialogType dialogType, string messageText,
        string defaultPromptText, IJsDialogCallback callback, ref bool suppressMessage)
    {
        if (_locked())
        {
            suppressMessage = true;
            return false;
        }
        return false;
    }

    public bool OnBeforeUnloadDialog(IWebBrowser chromiumWebBrowser, IBrowser browser, string messageText, bool isReload, IJsDialogCallback callback)
    {
        if (!_locked()) return false;
        using (callback) callback.Continue(true);
        return true;
    }

    public void OnResetDialogState(IWebBrowser chromiumWebBrowser, IBrowser browser) { }

    public void OnDialogClosed(IWebBrowser chromiumWebBrowser, IBrowser browser) { }
}
