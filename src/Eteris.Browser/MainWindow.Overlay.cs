using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Eteris.Browser;

// Camada por cima da página. Um motor desenhado numa janela própria (o motor de vídeo, e o principal em modo
// janela) tapa tudo o que o WPF desenhe na mesma janela por cima dele. Por isso os avisos, barras e painéis que
// ficam sobre a página vivem numa janela transparente, presa à janela principal e com a mesma área; onde não há
// nada desenhado os cliques passam para baixo (janela em camadas, transparência por pixel). A janela só está
// visível enquanto algum desses elementos estiver à vista — de resto não existe nada por cima da página.
public partial class MainWindow
{
    private Window? _overlay;
    private Grid? _overlayRoot;

    // Elementos da janela principal que passam para a camada (os nomes continuam a funcionar no código).
    private static readonly string[] OverlayElementNames =
        ["QrOverlay", "GlanceOverlay", "SplitFocusRing", "SplitDropHint", "MiniBar", "PermissionBar", "FindBar", "FocusToast", "LinkStatusBubble", "Toast", "PaletteOverlay"];

    // Onde se acrescentam camadas criadas em código (ex.: a folha de atalhos).
    private Grid OverlayRoot => _overlayRoot ?? (Grid)RootBorder.Child;

    private void SetUpOverlayLayer()
    {
        if (_overlay is not null) return;
        // Braço «puro» das medições de desempenho (só com ETERIS_TEST_PURO): sem a janela em camadas, os elementos ficam
        // na janela principal (tapados pela página; só para medir).
        if (Infrastructure.PerfDiagnostics.Without(Infrastructure.PerfLayers.Overlay)) return;
        var root = new Grid();
        foreach (var source in new[] { TitleRow, ToolbarRow })
        {
            var row = new RowDefinition();
            BindingOperations.SetBinding(row, RowDefinition.HeightProperty,
                new Binding(nameof(RowDefinition.ActualHeight)) { Source = source, Converter = PixelsToGridLength.Instance });
            root.RowDefinitions.Add(row);
        }
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var host = (Grid)RootBorder.Child;
        // Ajuste e5 (MainWindow.LinkBubble.cs): a bolha do link numa janela própria e o contorno do ecrã dividido na
        // janela principal; a camada já não fica ligada por causa deles. A janela da bolha só se cria no 1.º link (regra
        // R-2: nada de novo antes da 1.ª página; isto corre no ContentRendered).
        var ownBubble = Infrastructure.PerfTuning.IsOn(Infrastructure.PerfTuning.OwnLinkBubble);
        foreach (var name in OverlayElementNames)
        {
            if (StaysOutOfOverlay(name, ownBubble)) continue;
            if (FindName(name) is not FrameworkElement element) continue;
            host.Children.Remove(element);
            root.Children.Add(element);
        }
        _overlay = new Window
        {
            Owner = this,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Title = Title,
            FontFamily = FontFamily,
            UseLayoutRounding = UseLayoutRounding,
            DataContext = DataContext,
            Content = root,
        };
        // Mesmos nomes, recursos (tema, estilos) e atalhos que a janela principal.
        NameScope.SetNameScope(_overlay, NameScope.GetNameScope(this));
        _overlay.Resources.MergedDictionaries.Add(Resources);
        _overlay.PreviewKeyDown += Window_PreviewKeyDown;
        // O Alt largado com a camada ativa (o cartão do Espreitar aberto por Alt+clique fica com o teclado) também conta:
        // sem isto o «Alt em baixo» ficava preso e o Alt+clique seguinte, já com o cartão fechado, não abria nada.
        _overlay.PreviewKeyUp += (_, e) => AltKeyUp(e.Key == Key.System ? e.SystemKey : e.Key);
        _overlay.PreviewMouseDown += OnSideMouseButton;
        _overlayRoot = root;

        foreach (UIElement child in root.Children) WatchOverlayChild(child);
        PlaceAurora();
        foreach (var property in new[] { TitleRowHeightProperty, ToolbarRowHeightProperty })
            DependencyPropertyDescriptor.FromProperty(property, typeof(MainWindow)).AddValueChanged(this, (_, _) => PlaceAurora());
        LocationChanged += (_, _) => SyncOverlayBounds();
        SizeChanged += (_, _) => SyncOverlayBounds();
        RootBorder.SizeChanged += (_, _) => SyncOverlayBounds();
        PageFrame.SizeChanged += (_, _) => SyncOverlayBounds();
        StateChanged += (_, _) => { SyncOverlayBounds(); UpdateOverlayVisibility(); };
        // Esquecida antes de fechar: uma vigilância de Visibility que ainda corra (um RefreshBars na fila) encontra null
        // em UpdateOverlayVisibility e não chama Show() numa janela fechada.
        Closed += (_, _) =>
        {
            var closing = _overlay;
            _overlay = null;
            closing?.Close();
        };
        UpdateOverlayVisibility();
    }

    // Barra de carregamento: na janela principal, no fundo das barras, logo acima da página (fora dela). Na camada,
    // uma janela em camadas do tamanho da janela inteira, cada fotograma da animação obrigava a redesenhar e enviar a
    // camada toda (UpdateLayeredWindow) e o DWM a recompô-la por cima da página: ~18% do processador do Éteris ao
    // carregar (docs/motor.md, F4). Só com as barras escondidas (sem onde a pôr) volta para a camada, no topo da página.
    private void PlaceAurora()
    {
        if (_overlayRoot is null) return;
        var main = (Grid)RootBorder.Child;
        var inChrome = TitleRowHeight + ToolbarRowHeight >= AuroraBar.Height;
        var target = inChrome ? main : _overlayRoot;
        if (!ReferenceEquals(AuroraBar.Parent, target))
        {
            (AuroraBar.Parent as Panel)?.Children.Remove(AuroraBar);
            target.Children.Add(AuroraBar);
            if (!inChrome) WatchOverlayChild(AuroraBar);
        }
        Grid.SetColumn(AuroraBar, 0);
        Grid.SetColumnSpan(AuroraBar, 1);
        Grid.SetRow(AuroraBar, inChrome ? 0 : 2);
        Grid.SetRowSpan(AuroraBar, inChrome ? 2 : 1);
        AuroraBar.VerticalAlignment = inChrome ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        SyncOverlayBounds();
        UpdateOverlayVisibility();
    }

    private void WatchOverlayChild(UIElement child)
    {
        foreach (var property in new[] { VisibilityProperty, OpacityProperty })
            DependencyPropertyDescriptor.FromProperty(property, child.GetType()).AddValueChanged(child, (_, _) => UpdateOverlayVisibility());
    }

    // Camadas criadas em código depois do arranque (ex.: a folha de atalhos).
    private void AddToOverlay(UIElement element)
    {
        OverlayRoot.Children.Add(element);
        if (_overlayRoot is not null) WatchOverlayChild(element);
        // Um elemento já visível ao entrar não dispara a vigilância: a janela da camada tem de aparecer já.
        UpdateOverlayVisibility();
    }

    // Camadas temporárias (ex.: a caixa de mudar o nome da aba) saem da camada quando fecham.
    private void RemoveFromOverlay(UIElement element)
    {
        OverlayRoot.Children.Remove(element);
        UpdateOverlayVisibility();
    }

    private void UpdateOverlayVisibility()
    {
        if (_overlay is null || _overlayRoot is null) return;
        var needed = OverlayNeeded();
        AnimateAurora(AuroraBar.IsVisible && WindowState != WindowState.Minimized);
        if (needed && !_overlay.IsVisible)
        {
            SyncOverlayBounds();
            _overlay.Show();
            Infrastructure.PerfCounters.Add("camada.mostrar");
        }
        // Janela minimizada: não há fotograma a pintar (nem a mostrar) — sai já.
        else if (!needed && _overlay.IsVisible && WindowState == WindowState.Minimized)
        {
            _overlay.Hide();
            Infrastructure.PerfCounters.Add("camada.esconder");
        }
        else if (!needed && _overlay.IsVisible && !_overlayHidePending)
        {
            // Esconder só depois de a camada ter pintado vazia: escondida no mesmo instante, a janela em camadas
            // guardava a última imagem (o cartão do Espreitar acabado de fechar), e o próximo Show — a bolha do link
            // ao passar o rato — mostrava-a durante um fotograma antes de pintar o conteúdo novo.
            _overlayHidePending = true;
            var frames = 0;
            void AfterEmptyFrame(object? sender, EventArgs e)
            {
                if (++frames < OverlayHideFrames) return;
                CompositionTarget.Rendering -= AfterEmptyFrame;
                _overlayHidePending = false;
                if (_overlay is { IsVisible: true } overlay && !OverlayNeeded())
                {
                    overlay.Hide();
                    Infrastructure.PerfCounters.Add("camada.esconder");
                }
            }
            CompositionTarget.Rendering += AfterEmptyFrame;
        }
    }

    // Fotogramas pintados (vazios) antes de esconder a camada: o 1.º leva a imagem vazia, o 2.º garante que já chegou.
    private const int OverlayHideFrames = 2;
    private bool _overlayHidePending;

    private bool OverlayNeeded()
        => _overlayRoot is not null && WindowState != WindowState.Minimized
           && _overlayRoot.Children.OfType<UIElement>().Any(c => c.Visibility == Visibility.Visible && c.Opacity > 0.01);

    private void SyncOverlayBounds()
    {
        if (_overlay is null || PresentationSource.FromVisual(RootBorder) is not { CompositionTarget: { } target }) return;
        var topLeft = target.TransformFromDevice.Transform(RootBorder.PointToScreen(new Point(0, 0)));
        _overlay.Left = topLeft.X;
        _overlay.Top = topLeft.Y;
        _overlay.Width = RootBorder.ActualWidth;
        _overlay.Height = RootBorder.ActualHeight;
        // A barra de carregamento acompanha a moldura da página (margem da barra lateral e espaçamento).
        AuroraBar.Margin = new Thickness(PageFrame.Margin.Left + PageFrame.Padding.Left, 0, PageFrame.Margin.Right + PageFrame.Padding.Right, 0);
    }

    // Uma caixa de texto da camada só recebe o teclado com a janela da camada ativa.
    private void FocusInOverlay(UIElement element)
    {
        if (_overlay is { IsVisible: true } overlay && !overlay.IsActive) overlay.Activate();
        element.Focus();
    }

    // Ao fechar uma barra ou painel da camada, o teclado volta para a página.
    private void ReturnFocusToPage()
    {
        if (_overlay is { IsActive: true }) Activate();
        if (ActiveBrowser is { } browser) Keyboard.Focus(browser);
        else if (ActiveVideo is { } video) video.Focus();
    }

    private sealed class PixelsToGridLength : IValueConverter
    {
        public static readonly PixelsToGridLength Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => new GridLength(value is double pixels ? pixels : 0);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
