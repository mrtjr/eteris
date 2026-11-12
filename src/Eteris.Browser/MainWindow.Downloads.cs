using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CefSharp;
using Eteris.Browser.Browser;
using Eteris.Browser.Downloads;

namespace Eteris.Browser;

public partial class MainWindow
{
    private DownloadsViewModel Downloads { get; } = new();
    private DownloadHandler? _downloadHandler;
    // Quando o painel abriu sem o utilizador o pedir (null: abriu pelo botão); ver DownloadRisk.AcceptsClick.
    private DateTime? _downloadsOpenedUtc;

    private DownloadHandler DownloadHandler => _downloadHandler ??= CreateDownloadHandler();

    private DownloadHandler CreateDownloadHandler()
    {
        DownloadsList.ItemsSource = Downloads.Items;
        Downloads.PropertyChanged += OnDownloadsChanged;
        return new DownloadHandler(Downloads, Dispatcher, (source, url) =>
        {
            DownloadsHost.Visibility = Visibility.Visible;
            // O painel abriu sozinho (ou ganhou uma linha nova): durante 1 s um clique não abre nada.
            _downloadsOpenedUtc = DateTime.UtcNow;
            DownloadsPopup.SetCurrentValue(Popup.PlacementTargetProperty, DownloadsButton);
            DownloadsPopup.IsOpen = true;

            // Aba aberta diretamente num link de transferência: não fica uma página branca (como no Chrome).
            var tab = _browsers.FirstOrDefault(p => ReferenceEquals(p.Value, source)).Key;
            if (tab is not null && !tab.CanGoBack && string.Equals(tab.Address, url, StringComparison.OrdinalIgnoreCase))
            {
                tab.Address = NewTabUrl;
                tab.Title = "Nova aba";
                _browsers[tab].Load(NewTabUrl);
            }
            // «Continuar sem segurança» num link http:// que afinal é uma transferência: a aba ficava na página branca
            // de «Continuar». Volta à página de onde se partiu (o passo pela entrada https:// que falhou é saltado).
            else if (tab is not null && _browsers.TryGetValue(tab, out var browser) && browser.Address is { } raw
                     && ErrorPage.IsErrorPage(raw) && ErrorPage.FailedUrl(raw) is { } shown && ErrorPage.SameAddress(shown, url)
                     && browser.CanGoBack)
                browser.Back();
        }, DivertAltClickDownload, PlayInsteadOfDownload);
    }

    // Uma «transferência» que é afinal vídeo/áudio para ver (Tabs/VideoEngine.cs, IsPlayableDownload): abre no motor de
    // vídeo, na aba que a pediu. Corre na thread do CEF.
    private bool PlayInsteadOfDownload(CefSharp.IWebBrowser source, string url)
    {
        if (source is not CefSharp.Wpf.HwndHost.ChromiumWebBrowser) return false;
        Dispatcher.InvokeAsync(() =>
        {
            var tab = _browsers.FirstOrDefault(p => ReferenceEquals(p.Value, source)).Key;
            // Uma página fora das abas (painel da barra lateral): abre numa aba nova, que faz o mesmo caminho.
            if (tab is not null) SwitchToVideoEngine(tab, url);
            else Tabs.NewTab(url);
        });
        return true;
    }

    // Alt+clique num link que o script do Éteris não apanhou (num iframe de outra origem, que vive noutro processo):
    // o Chromium transforma-o numa transferência, que é recusada e vira um cartão «Espreitar» — com o mesmo Alt
    // verdadeiro que as abas exigem. Corre na thread do CEF: o Alt é lido do teclado nesse instante.
    private bool DivertAltClickDownload(CefSharp.IWebBrowser source, string url)
    {
        var altOnly = KeyHeld(VirtualKeyAlt) && !KeyHeld(VirtualKeyControl) && !KeyHeld(VirtualKeyShift);
        if (!LinkPreview.DivertsDownload(altOnly, url)) return false;
        Dispatcher.InvokeAsync(() =>
        {
            if (source is not UIElement { IsVisible: true }) return;
            if (_altTap.TakeGesture(DateTime.UtcNow, LinkPreview.GestureWindow)) ShowLinkPreview(url);
        });
        return true;
    }

    private const int VirtualKeyShift = 0x10, VirtualKeyControl = 0x11, VirtualKeyAlt = 0x12;

    // GetAsyncKeyState está declarado em MainWindow.Workspaces.cs.
    private static bool KeyHeld(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private void OnDownloadsChanged(object? sender, PropertyChangedEventArgs e)
    {
        var active = Downloads.HasActive;
        DownloadsTrack.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        DownloadsRing.Data = active ? RingArc(Downloads.OverallPercent) : null;

        TaskbarItemInfo ??= new System.Windows.Shell.TaskbarItemInfo();
        var (state, value) = TaskbarProgress.For(active, Downloads.OverallPercent);
        TaskbarItemInfo.ProgressState = state;
        TaskbarItemInfo.ProgressValue = value;
        if (TaskbarProgress.ShouldFlash(_hadActiveDownloads, active, IsActive)) FlashTaskbar();
        _hadActiveDownloads = active;
    }

    private bool _hadActiveDownloads;

    // Pisca até a janela voltar ao primeiro plano (Infrastructure/WindowAttention).
    private void FlashTaskbar() => Infrastructure.WindowAttention.Flash(new System.Windows.Interop.WindowInteropHelper(this).Handle);

    // Arco do anel de progresso à volta do botão (raio 15, começa no topo, sentido horário).
    private static Geometry? RingArc(int percent)
    {
        if (percent <= 0) return null;
        const double r = 15, c = 17;
        var angle = Math.Min(percent, 99.9) / 100 * 2 * Math.PI;
        var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = new Point(c, c - r) };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, angle > Math.PI, SweepDirection.Clockwise, true));
        return new PathGeometry([figure]);
    }

    private void ToggleDownloads()
    {
        // Sem botão próprio na barra, o painel ancora no MenuButton: nunca por cima do menu aberto.
        CloseBrowserMenu();
        DownloadsPopup.SetCurrentValue(Popup.PlacementTargetProperty, DownloadsHost.IsVisible ? DownloadsButton : MenuButton);
        DownloadsPopup.IsOpen = !DownloadsPopup.IsOpen;
        _downloadsOpenedUtc = null;
    }

    private void Downloads_Click(object sender, RoutedEventArgs e) => ToggleDownloads();

    private void OpenDownloadsFolder_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo(DownloadPaths.DownloadsFolder()) { UseShellExecute = true });

    private void DownloadRow_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadEntry { IsComplete: true } entry || !File.Exists(entry.FullPath)) return;
        if (!DownloadRisk.AcceptsClick(DateTime.UtcNow, _downloadsOpenedUtc, entry.CompletedAtUtc)) return;
        // Programas e scripts: o nome inteiro e a origem à vista antes de correrem (o botão por defeito é «Não»).
        if (DownloadRisk.Executes(entry.FileName)
            && MessageBox.Show(this, DownloadRisk.Question(entry.FileName, entry.Source), "Abrir este ficheiro?",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        Process.Start(new ProcessStartInfo(entry.FullPath) { UseShellExecute = true });
    }

    private void ShowDownloadInFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadEntry entry && File.Exists(entry.FullPath))
            Process.Start("explorer.exe", $"/select,\"{entry.FullPath}\"");
        e.Handled = true;
    }

    private void CancelDownload_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadEntry entry) DownloadHandler.Cancel(entry.Id);
        e.Handled = true;
    }
}
