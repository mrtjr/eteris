using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Eteris.Browser.Theme;

namespace Eteris.Browser;

// Interface que desaparece (modo foco e ecrã inteiro escolhido no menu ou com F11): as barras aparecem quando o
// rato toca no topo da janela e escondem-se pouco depois de o deixar. A página é uma janela nativa que não dá ao
// WPF os movimentos do rato, por isso a posição do cursor é lida com GetCursorPos num temporizador que só corre
// enquanto as barras estão escondidas. As barras reveladas ocupam espaço (a página encolhe) em vez de ficarem por
// cima: nada é desenhado sobre a janela da página fora da camada de sobreposição.
public partial class MainWindow
{
    private readonly AutoHideBars _autoHide = new();
    private DispatcherTimer? _autoHideTimer;

    // Modos em que as barras se escondem mas podem ser chamadas pelo rato. No ecrã inteiro pedido pela própria
    // página (um vídeo) não: como no Chrome, o vídeo ocupa tudo até se sair com Esc.
    private bool BarsAutoHide => _focusMode || (_pageFullscreen && !_fullscreenByPage);

    private void SyncAutoHide()
    {
        if (BarsAutoHide)
        {
            if (_autoHideTimer is not null) return;
            _autoHideTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
            _autoHideTimer.Tick += (_, _) => AutoHideTick();
            _autoHideTimer.Start();
        }
        else
        {
            _autoHideTimer?.Stop();
            _autoHideTimer = null;
            _autoHide.Hide();
        }
    }

    private void AutoHideTick()
    {
        if (!BarsAutoHide || WindowState == WindowState.Minimized || !IsVisible) return;
        var before = _autoHide.Revealed;
        var (inside, cursorY) = CursorInWindow();
        var barsHeight = ChromeLayout.Resolve(VerticalTabs, CompactDensity, hidden: false).TitleHeight
                         + ChromeLayout.Resolve(VerticalTabs, CompactDensity, hidden: false).ToolbarHeight;
        var revealed = _autoHide.Update(cursorY, inside, barsHeight, BarsInUse(), DateTime.UtcNow);
        if (revealed != before) ApplyChromeLayout();
    }

    // As barras têm algo em uso: não desaparecem debaixo do utilizador.
    private bool BarsInUse()
        => Omnibox.IsKeyboardFocusWithin || SuggestionPopup.IsOpen || BrowserMenuOpen || ShieldPopup.IsOpen || MediaPopup.IsOpen
           || DownloadsPopup.IsOpen || SiteInfoPopup.IsOpen || TabListPopup.IsOpen || _dragging;

    // Um atalho que precisa das barras (barra de endereço, nova aba) mostra-as por uns segundos.
    private void RevealBarsForKeyboard()
    {
        if (!BarsAutoHide) return;
        _autoHide.RevealFor(AutoHideBars.KeyboardHold, DateTime.UtcNow);
        ApplyChromeLayout();
        // A barra de endereço só recebe o foco depois de estar visível: deixa o layout acontecer.
        UpdateLayout();
    }

    private (bool Inside, double Y) CursorInWindow()
    {
        if (!GetCursorPos(out var point) || PresentationSource.FromVisual(RootBorder) is null) return (false, 0);
        var local = RootBorder.PointFromScreen(new Point(point.X, point.Y));
        var inside = local.X >= 0 && local.Y >= 0 && local.X < RootBorder.ActualWidth && local.Y < RootBorder.ActualHeight;
        return (inside, local.Y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);
}
