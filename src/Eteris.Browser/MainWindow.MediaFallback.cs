using System.Diagnostics;
using System.Windows;
using Eteris.Browser.Data;
using Eteris.Browser.Shields;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// Vídeo num formato que o Éteris não pode incluir: barra com «Abrir no navegador do sistema».
public partial class MainWindow
{
    private BrowserTab? _mediaFallbackTab;
    private string? _mediaFallbackUrl;
    private readonly HashSet<string> _mediaFallbackDismissed = new(StringComparer.OrdinalIgnoreCase);

    // Scripts que correm no início de cada documento: escurecer (se o site estiver escurecido) e o vigia de vídeo.
    private static string? EarlyScripts(string url)
    {
        if (!DarkPages.IsCandidate(url)) return null;
        var dark = WebHost(url) is { } host && AppData.IsDark(host) ? DarkPages.EarlyScript : null;
        return dark is null ? MediaFallback.WatchScript : dark + "\n;\n" + MediaFallback.WatchScript;
    }

    private void WatchMediaFallback()
        => Tabs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TabsViewModel.Active) && !ReferenceEquals(Tabs.Active, _mediaFallbackTab)) HideMediaFallback();
        };

    private void OnMediaUnsupported(BrowserTab tab, string reason)
    {
        if (!ReferenceEquals(tab, Tabs.Active) || WebHost(tab.Address) is not { } host || _mediaFallbackDismissed.Contains(host)) return;
        _mediaFallbackTab = tab;
        _mediaFallbackUrl = tab.Address;
        MediaFallbackText.Text = (reason == "format"
                ? "Este vídeo usa um formato patenteado (H.264/AAC) que o Éteris não pode incluir."
                : "Se este vídeo não abrir, é provável que o site só o ofereça em formatos patenteados (H.264/AAC), que o Éteris não pode incluir.")
            + (IsPrivate ? " Atenção: ao abrir no navegador do sistema, sai da janela privada." : string.Empty);
        MediaFallbackBar.Visibility = Visibility.Visible;
    }

    private void HideMediaFallback()
    {
        MediaFallbackBar.Visibility = Visibility.Collapsed;
        _mediaFallbackTab = null;
    }

    private void MediaFallbackOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaFallbackUrl is not { } url) return;
        HideMediaFallback();
        try
        {
            Process.Start(new ProcessStartInfo(MediaFallback.SystemBrowserTarget(MediaFallback.DefaultBrowserProgId(), url)) { UseShellExecute = true });
        }
        catch (Exception) { ShowToast("Não foi possível abrir o navegador do sistema"); }
    }

    private void MediaFallbackDismiss_Click(object sender, RoutedEventArgs e)
    {
        if (WebHost(_mediaFallbackUrl) is { } host) _mediaFallbackDismissed.Add(host);
        HideMediaFallback();
    }
}
