using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser;

// E5 do plano de desempenho da 0.2.14 (ajuste e5). A bolha do link e o contorno do ecrã dividido viviam na camada por
// cima da página (MainWindow.Overlay.cs), uma janela em camadas do tamanho da janela inteira: cada link novo debaixo do
// rato redesenhava e enviava a camada toda (UpdateLayeredWindow) e o DWM recompunha-a por cima da página; o contorno
// deixava-a ligada o tempo todo com o ecrã dividido. Agora:
//   - a bolha tem uma janela própria do tamanho do texto, presa à moldura, que nunca recebe a ativação nem os cliques
//     (WS_EX_NOACTIVATE, WS_EX_TRANSPARENT; como a bolha de estado do Chrome, um widget pequeno);
//   - o contorno é desenhado pelo WPF na janela principal, à volta da metade ativa e por baixo dela: a página (uma
//     janela própria) tapa o interior e vê-se a faixa de 2 px na margem entre as metades e na moldura.
// Com -e5 (ou no braço «puro» sem camada) tudo fica na camada, como até à 0.2.13.
public partial class MainWindow
{
    private Window? _linkBubble;
    // Onde a bolha fica, a partir do canto inferior esquerdo da área da página (MainWindow.Sidebar.cs muda-o).
    private Thickness _linkBubbleOffset = new(12, 0, 0, 12);

    // Quantos px o contorno da metade ativa sai para fora dela (a espessura do contorno).
    internal const double SplitRingOutset = 2;

    private bool OwnLinkBubble => _linkBubble is not null;

    // O contorno fica na janela principal: com o ajuste e com a camada criada (no braço «puro» sem camada, tudo fica na
    // janela principal como antes, por cima).
    private bool RingOutsideOverlay => PerfTuning.IsOn(PerfTuning.OwnLinkBubble) && _overlay is not null;

    // Elementos que, com o ajuste, não passam para a camada (MainWindow.Overlay.cs, SetUpOverlayLayer).
    internal static bool StaysOutOfOverlay(string name, bool tweak) => tweak && name is "LinkStatusBubble" or "SplitFocusRing";

    private void SetUpLinkBubble(Grid host)
    {
        if (_linkBubble is not null || !PerfTuning.IsOn(PerfTuning.OwnLinkBubble)) return;
        _linkBubbleOffset = LinkStatusBubble.Margin;
        (LinkStatusBubble.Parent as Panel ?? host).Children.Remove(LinkStatusBubble);
        LinkStatusBubble.Margin = new Thickness(0);
        LinkStatusBubble.HorizontalAlignment = HorizontalAlignment.Left;
        LinkStatusBubble.VerticalAlignment = VerticalAlignment.Top;
        var bubble = new Window
        {
            Owner = this,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            IsHitTestVisible = false,
            Title = Title,
            FontFamily = FontFamily,
            UseLayoutRounding = UseLayoutRounding,
            Content = LinkStatusBubble,
        };
        bubble.Resources.MergedDictionaries.Add(Resources);
        bubble.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(bubble).Handle;
            var style = GetWindowLongPtr(hwnd, GwlExStyle);
            SetWindowLongPtr(hwnd, GwlExStyle, style | WsExNoActivate | WsExTransparent | WsExToolWindow);
        };
        // A bolha muda de largura com o texto: o canto inferior esquerdo fica no mesmo sítio.
        bubble.SizeChanged += (_, _) => PlaceLinkBubble();
        LocationChanged += (_, _) => PlaceLinkBubble();
        SizeChanged += (_, _) => PlaceLinkBubble();
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) HideLinkBubble(); };
        Deactivated += (_, _) => HideLinkBubble();
        Closed += (_, _) => bubble.Close();
        _linkBubble = bubble;
    }

    // O texto já está na bolha: mostra-a (sem ativar) ou esconde-a. A janela nasce aqui, no 1.º link a mostrar, e não ao
    // criar a camada (ContentRendered, antes da 1.ª página: regra R-2). Até lá a bolha fica na janela principal, escondida;
    // sem a camada (braço «puro») fica lá sempre, como até à 0.2.13.
    private void SyncLinkBubble(bool show)
    {
        if (_linkBubble is null && show && _overlay is not null) SetUpLinkBubble((Grid)RootBorder.Child);
        if (_linkBubble is null) return;
        if (!show || WindowState == WindowState.Minimized || !IsVisible)
        {
            HideLinkBubble();
            return;
        }
        if (!_linkBubble.IsVisible)
        {
            _linkBubble.Show();
            PerfCounters.Add("bolha.mostrar");
        }
        PlaceLinkBubble();
    }

    private void HideLinkBubble()
    {
        if (_linkBubble is not { IsVisible: true } bubble) return;
        bubble.Hide();
        PerfCounters.Add("bolha.esconder");
    }

    private void PlaceLinkBubble()
    {
        if (_linkBubble is not { IsVisible: true } bubble || PresentationSource.FromVisual(RootBorder) is not { CompositionTarget: { } target }) return;
        var topLeft = target.TransformFromDevice.Transform(RootBorder.PointToScreen(new Point(0, 0)));
        var place = LinkBubblePlace(new Rect(topLeft, new Size(RootBorder.ActualWidth, RootBorder.ActualHeight)), _linkBubbleOffset,
            new Size(bubble.ActualWidth, bubble.ActualHeight));
        bubble.Left = place.X;
        bubble.Top = place.Y;
    }

    // Canto superior esquerdo da bolha (DIPs do ecrã): no fundo da área da página, «offset» acima e à direita do canto
    // inferior esquerdo — o mesmo sítio que a bolha tinha na camada (linha da página, em baixo à esquerda).
    internal static Point LinkBubblePlace(Rect host, Thickness offset, Size bubble)
        => new(host.Left + offset.Left, host.Bottom - offset.Bottom - bubble.Height);

    // A barra lateral mexe na margem da área da página: a bolha segue-a (na janela própria ou na camada).
    private void SetLinkBubbleOffset(Thickness offset)
    {
        _linkBubbleOffset = offset;
        if (OwnLinkBubble) PlaceLinkBubble();
        else LinkStatusBubble.Margin = offset;
    }

    // Contorno do ecrã dividido: na camada, por cima da metade (até à 0.2.13); com o ajuste, na janela principal, 2 px para
    // fora da metade e por baixo da página.
    internal static Rect SplitRingBounds(Point origin, Size pane, bool outside)
        => outside
            ? new Rect(origin.X - SplitRingOutset, origin.Y - SplitRingOutset, pane.Width + 2 * SplitRingOutset, pane.Height + 2 * SplitRingOutset)
            : new Rect(origin, pane);

    private const int GwlExStyle = -20;
    private const nint WsExNoActivate = 0x08000000;
    private const nint WsExTransparent = 0x00000020;
    private const nint WsExToolWindow = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
}
