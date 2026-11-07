using System.Windows;
using System.Windows.Media;
using CefSharp;
using Eteris.Browser.Browser;

namespace Eteris.Browser;

public partial class MainWindow
{
    private IRequestContext? _privateContext;

    public bool IsPrivate { get; }

    private string NewTabUrl => IsPrivate ? NewTabPage.PrivateUrl : NewTabPage.Url;

    // Contexto sem CachePath: cookies, cache e armazenamento ficam só em memória e acabam com a janela.
    private IRequestContext? PrivateContext => IsPrivate
        ? _privateContext ??= CreatePrivateContext()
        : null;

    private static IRequestContext CreatePrivateContext()
    {
        var context = new RequestContext(new RequestContextSettings());
        // As fábricas de esquemas são por contexto: sem isto eteris:// dá ERR_UNKNOWN_URL_SCHEME na janela privada.
        context.RegisterSchemeHandlerFactory(EterisSchemeHandlerFactory.SchemeName, string.Empty, new EterisSchemeHandlerFactory(privateContext: true));
        _ = Shields.PrivacyPreferences.ApplyAsync(context);
        return context;
    }

    private void ConfigurePrivateWindow()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x14, 0x0B, 0x24));
        PrivateBadge.Visibility = Visibility.Visible;
        Closed += (_, _) => _privateContext?.Dispose();
    }

    internal static void OpenWindow(bool isPrivate, string? url = null)
    {
        var window = new MainWindow(url, isPrivate);
        window.Show();
        window.Activate();
    }
}
