using System.Windows;
using System.Windows.Media;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Theme;

namespace Eteris.Browser;

public partial class MainWindow
{
    private const byte ChromeAlpha = 0xC4;
    private bool _mica;

    private Brush OpaqueFrame => IsPrivate
        ? new SolidColorBrush(Color.FromRgb(0x14, 0x0B, 0x24))
        : (Brush)FindResource("FrameBrush");

    private void ApplyBackdrop()
    {
        // Braço «puro» das medições de desempenho (só com ETERIS_TEST_PURO): moldura opaca, como sem o Mica.
        var mica = Settings.Current.Mica && !PerfDiagnostics.Without(PerfLayers.Mica);
        if (mica && !_mica)
        {
            _mica = WindowEffects.TryEnableMica(this);
            // Numa janela privada, um véu violeta por cima do material mantém-na reconhecível.
            if (_mica && IsPrivate) Background = new SolidColorBrush(Color.FromArgb(0x99, 0x24, 0x0B, 0x40));
        }
        else if (!mica && _mica)
        {
            WindowEffects.DisableMica(this, OpaqueFrame);
            _mica = false;
        }
        WindowEffects.PaintUnderChildWindows(this);
        ApplyTint();
    }

    // Cor por baixo da página à vista (Theme/PageBackdrop.cs): é a que aparece num fotograma ainda vazio do Chromium.
    private void SyncPageBackdrop()
    {
        var address = Tabs.Active?.Address;
        var darkened = !IsPrivate && address is not null && Shields.DarkPages.IsCandidate(address) && WebHost(address) is { } host && AppData.IsDark(host);
        var color = PageBackdrop.For(address, UsesLight, darkened);
        if (BrowserHost.Background is SolidColorBrush { Color: var current } && current == color) return;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        BrowserHost.Background = brush;
    }

    private void WatchBackdropSetting()
    {
        void OnChanged(UserSettings _) => Dispatcher.InvokeAsync(ApplyBackdrop);
        Settings.Changed += OnChanged;
        Closed += (_, _) => Settings.Changed -= OnChanged;
    }
}
