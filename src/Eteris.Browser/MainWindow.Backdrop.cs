using System.Windows;
using System.Windows.Media;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;

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
        if (Settings.Current.Mica && !_mica)
        {
            _mica = WindowEffects.TryEnableMica(this);
            // Numa janela privada, um véu violeta por cima do material mantém-na reconhecível.
            if (_mica && IsPrivate) Background = new SolidColorBrush(Color.FromArgb(0x99, 0x24, 0x0B, 0x40));
        }
        else if (!Settings.Current.Mica && _mica)
        {
            WindowEffects.DisableMica(this, OpaqueFrame);
            _mica = false;
        }
        ApplyTint();
    }

    private void WatchBackdropSetting()
    {
        void OnChanged(UserSettings _) => Dispatcher.InvokeAsync(ApplyBackdrop);
        Settings.Changed += OnChanged;
        Closed += (_, _) => Settings.Changed -= OnChanged;
    }
}
