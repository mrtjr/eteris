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

    private DownloadHandler DownloadHandler => _downloadHandler ??= CreateDownloadHandler();

    private DownloadHandler CreateDownloadHandler()
    {
        DownloadsList.ItemsSource = Downloads.Items;
        Downloads.PropertyChanged += OnDownloadsChanged;
        return new DownloadHandler(Downloads, Dispatcher, (source, url) =>
        {
            DownloadsHost.Visibility = Visibility.Visible;
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
        }, DivertAltClickDownload);
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

    private void FlashTaskbar()
    {
        var info = new FLASHWINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<FLASHWINFO>(),
            hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle,
            dwFlags = 0x2 | 0xC, // FLASHW_TRAY | FLASHW_TIMERNOFG: pisca até a janela voltar ao primeiro plano
            uCount = 3,
        };
        FlashWindowEx(ref info);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public nint hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO info);

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
    }

    private void Downloads_Click(object sender, RoutedEventArgs e) => ToggleDownloads();

    private void OpenDownloadsFolder_Click(object sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo(DownloadPaths.DownloadsFolder()) { UseShellExecute = true });

    private void DownloadRow_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadEntry { IsComplete: true } entry && File.Exists(entry.FullPath))
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
