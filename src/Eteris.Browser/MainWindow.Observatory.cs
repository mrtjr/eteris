using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using Eteris.Browser.Shields;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// Observatório do site: quem está a observar a página, ao vivo. O site ao centro e, à volta, cada domínio de terceiros
// como um nó (vermelho bloqueado, verde permitido, âmbar exceção sua), agrupados por empresa; à direita a lista por
// empresa e o detalhe do domínio escolhido. Vive na camada de sobreposição (a página é uma janela nativa e taparia o
// WPF) e redesenha-se em lotes de 250 ms só quando a aba registou pedidos novos.
public partial class MainWindow
{
    private const double ObservatorySize = 440;

    private Grid? _observatory;
    private Canvas? _obsCanvas;
    private StackPanel? _obsList;
    private StackPanel? _obsDetail;
    private TextBlock? _obsTitle;
    private TextBlock? _obsStats;
    private TextBlock? _obsCenterText;
    private TextBlock? _obsHidden;
    private Style? _obsRowStyle;
    private DispatcherTimer? _obsTimer;
    private BrowserTab? _obsTab;
    private int _obsVersion = -1;
    private string? _obsSelected;
    private string? _obsWhyHost;
    private ObservatorySnapshot _obsSnapshot = ObservatorySnapshot.Empty;
    private readonly Dictionary<string, ObservatoryVisual> _obsVisuals = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<UIElement> _obsLabels = [];

    private sealed class ObservatoryVisual(Ellipse node, Line line, ScaleTransform scale)
    {
        public Ellipse Node { get; } = node;
        public Line Line { get; } = line;
        public ScaleTransform Scale { get; } = scale;
        public int Requests { get; set; }
    }

    private bool ObservatoryOpen => _observatory is { Visibility: Visibility.Visible };

    private void ToggleObservatory()
    {
        if (ObservatoryOpen) CloseObservatory();
        else OpenObservatory();
    }

    private void OpenObservatory()
    {
        if (Tabs.Active is not { } tab) return;
        ShieldPopup.IsOpen = false;
        CloseBrowserMenu();
        CloseRadar();
        ClosePalette();
        CloseShortcuts();
        CloseSuggestions();
        _observatory ??= BuildObservatory();
        _obsTab = tab;
        _obsVersion = -1;
        _obsSelected = null;
        ClearObservatoryGraph();
        _observatory.Visibility = Visibility.Visible;
        BrowserHost.Effect = new BlurEffect { Radius = 18, RenderingBias = RenderingBias.Performance };
        var card = (FrameworkElement)_observatory.Children[1];
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        var scale = (ScaleTransform)card.RenderTransform;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        RefreshObservatory();
        if (_obsTimer is null)
        {
            _obsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => RefreshObservatory(), Dispatcher);
            // Fechar a janela com o painel aberto: o temporizador prendia a janela, as abas e os visuais até sair do Éteris.
            Closed += (_, _) => StopObservatoryTimer();
        }
        _obsTimer.Start();
        FocusInOverlay(card);
    }

    private void StopObservatoryTimer()
    {
        _obsTimer?.Stop();
        _obsTab = null;
    }

    private void CloseObservatory()
    {
        if (!ObservatoryOpen) return;
        _obsTimer?.Stop();
        _observatory!.Visibility = Visibility.Collapsed;
        BrowserHost.Effect = null;
        _obsTab = null;
        ReturnFocusToPage();
    }

    // As cores são lidas quando o painel é construído: com outro tema, constrói-se de novo.
    private void ResetObservatoryForTheme()
    {
        if (_observatory is null) return;
        CloseObservatory();
        OverlayRoot.Children.Remove(_observatory);
        _observatory = null;
        _obsVisuals.Clear();
        _obsLabels.Clear();
    }

    private Brush ObsRes(string key) => (Brush)FindResource(key);

    private static Brush NodeBrush(NodeColor color) => color switch
    {
        NodeColor.Blocked => BlockedBrush,
        NodeColor.User => WarningBrush,
        _ => AllowedBrush,
    };

    private Grid BuildObservatory()
    {
        var overlay = new Grid { Visibility = Visibility.Collapsed };
        // As linhas da lista usam o estilo das linhas do menu do Éteris (dicionário à parte, como no menu).
        var theme = new ResourceDictionary { Source = new Uri($"pack://application:,,,/{typeof(MainWindow).Assembly.GetName().Name};component/Theme/BrowserMenu.xaml", UriKind.Absolute) };
        overlay.Resources.MergedDictionaries.Add(theme);
        _obsRowStyle = (Style)theme["BrowserMenuButton"];
        Grid.SetRowSpan(overlay, 3);
        Panel.SetZIndex(overlay, 108);
        WindowChrome.SetIsHitTestVisibleInChrome(overlay, true);
        AutomationPropertiesName(overlay, "Observatório do site");

        var backdrop = new Rectangle { Fill = ObsRes("ScrimBrush") };
        backdrop.MouseLeftButtonDown += (_, _) => CloseObservatory();
        overlay.Children.Add(backdrop);

        // Gráfico: um quadrado fixo dentro de uma Viewbox (encolhe em janelas pequenas).
        _obsCanvas = new Canvas { Width = ObservatorySize, Height = ObservatorySize, ClipToBounds = false };
        var ring = new Ellipse
        {
            Width = ObservatorySize - 92, Height = ObservatorySize - 92, Stroke = ObsRes("FieldBorderBrush"), StrokeThickness = 1,
            StrokeDashArray = [2, 4], IsHitTestVisible = false,
        };
        Canvas.SetLeft(ring, 46);
        Canvas.SetTop(ring, 46);
        _obsCanvas.Children.Add(ring);
        var center = new Ellipse { Width = 58, Height = 58, Fill = ObsRes("IrisBrush"), IsHitTestVisible = false };
        Canvas.SetLeft(center, ObservatorySize / 2 - 29);
        Canvas.SetTop(center, ObservatorySize / 2 - 29);
        Panel.SetZIndex(center, 5);
        _obsCanvas.Children.Add(center);
        var glyph = new TextBlock
        {
            Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 22, Foreground = ObsRes("AccentInkBrush"),
            Width = 58, TextAlignment = TextAlignment.Center, IsHitTestVisible = false,
        };
        Canvas.SetLeft(glyph, ObservatorySize / 2 - 29);
        Canvas.SetTop(glyph, ObservatorySize / 2 - 13);
        Panel.SetZIndex(glyph, 6);
        _obsCanvas.Children.Add(glyph);
        _obsCenterText = new TextBlock
        {
            FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ObsRes("TextBrush"), Width = 160, TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false,
        };
        Canvas.SetLeft(_obsCenterText, ObservatorySize / 2 - 80);
        Canvas.SetTop(_obsCenterText, ObservatorySize / 2 + 34);
        Panel.SetZIndex(_obsCenterText, 6);
        _obsCanvas.Children.Add(_obsCenterText);

        var legend = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (brush, text) in new[] { (BlockedBrush, "bloqueado"), (AllowedBrush, "permitido"), (WarningBrush, "exceção sua") })
        {
            legend.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = brush, Margin = new Thickness(12, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            legend.Children.Add(new TextBlock { Text = text, FontSize = 11.5, Foreground = ObsRes("TextMutedBrush"), VerticalAlignment = VerticalAlignment.Center });
        }
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new Viewbox { Child = _obsCanvas, MaxWidth = ObservatorySize, MaxHeight = ObservatorySize, Stretch = Stretch.Uniform });
        left.Children.Add(legend);
        // Com mais domínios do que cabem no anel, os que ficam de fora estão só na lista: dito aqui, nunca em silêncio.
        _obsHidden = new TextBlock
        {
            FontSize = 11.5, Foreground = ObsRes("TextFaintBrush"), HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed,
        };
        left.Children.Add(_obsHidden);

        // Coluna da direita: título, números, lista por empresa e o detalhe do domínio.
        _obsTitle = new TextBlock { FontSize = 19, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        _obsStats = new TextBlock { FontSize = 12.5, Foreground = ObsRes("TextMutedBrush"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) };
        _obsList = new StackPanel();
        _obsDetail = new StackPanel();
        var close = new Button
        {
            Content = "", Style = (Style)FindResource("IconButton"), Width = 30, Height = 30, FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, ToolTip = "Fechar (Esc)",
        };
        close.Click += (_, _) => CloseObservatory();
        var header = new Grid();
        var titles = new StackPanel { Margin = new Thickness(0, 0, 36, 0) };
        titles.Children.Add(new TextBlock { Text = "OBSERVATÓRIO DO SITE", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = ObsRes("TextFaintBrush") });
        titles.Children.Add(_obsTitle);
        titles.Children.Add(_obsStats);
        header.Children.Add(titles);
        header.Children.Add(close);

        var detailCard = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 10, 0, 0),
            Background = ObsRes("HoverBrush"), Child = _obsDetail,
        };
        var right = new DockPanel { LastChildFill = true, Width = 330, Margin = new Thickness(26, 0, 0, 0) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(detailCard, Dock.Bottom);
        right.Children.Add(header);
        right.Children.Add(detailCard);
        right.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _obsList, Focusable = false });

        var body = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(right, Dock.Right);
        body.Children.Add(right);
        body.Children.Add(left);

        var card = new Border
        {
            MaxWidth = 900, MaxHeight = 620, Margin = new Thickness(40, 60, 40, 30), VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, CornerRadius = new CornerRadius(22), Padding = new Thickness(26, 22, 22, 20),
            Background = ObsRes("SurfaceRaisedBrush"), BorderBrush = ObsRes("IrisBrush"), BorderThickness = new Thickness(1.5),
            Effect = new DropShadowEffect { BlurRadius = 60, ShadowDepth = 18, Direction = 270, Opacity = 0.6, Color = Colors.Black },
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(), Focusable = true,
            FocusVisualStyle = null, Child = body,
        };
        card.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            CloseObservatory();
            e.Handled = true;
        };
        overlay.Children.Add(card);
        AddToOverlay(overlay);
        return overlay;
    }

    private static void AutomationPropertiesName(DependencyObject element, string name)
        => System.Windows.Automation.AutomationProperties.SetName(element, name);

    private void ClearObservatoryGraph()
    {
        if (_obsCanvas is null) return;
        foreach (var visual in _obsVisuals.Values)
        {
            _obsCanvas.Children.Remove(visual.Node);
            _obsCanvas.Children.Remove(visual.Line);
        }
        foreach (var label in _obsLabels) _obsCanvas.Children.Remove(label);
        _obsVisuals.Clear();
        _obsLabels.Clear();
    }

    private void RefreshObservatory()
    {
        if (_obsTab is not { } tab || _obsCanvas is null) return;
        // A aba fechou ou deixou de ser a ativa: o painel era dela.
        if (!ReferenceEquals(tab, Tabs.Active))
        {
            CloseObservatory();
            return;
        }
        var snap = tab.Observatory.Snapshot();
        if (snap.Version == _obsVersion) return;
        // Uma navegação nova recomeça o desenho.
        if (snap.PageUrl != _obsSnapshot.PageUrl) ClearObservatoryGraph();
        var first = _obsVisuals.Count == 0;
        _obsVersion = snap.Version;
        _obsSnapshot = snap;
        var groups = SiteObservatory.ByCompany(snap.ThirdParties);
        var drawing = ObservatoryLayout.Place(groups, ObservatorySize);

        _obsTitle!.Text = snap.Site ?? Tabs.Active?.Host ?? "Página";
        _obsCenterText!.Text = snap.Site ?? string.Empty;
        _obsStats!.Text = ObservatoryText.Stats(snap, groups);
        var hidden = ObservatoryText.HiddenNote(drawing.Hidden);
        _obsHidden!.Text = hidden ?? string.Empty;
        _obsHidden.Visibility = hidden is null ? Visibility.Collapsed : Visibility.Visible;
        DrawObservatory(drawing, first);
        FillObservatoryList(groups);
        ShowObservatoryDetail(_obsSelected);
    }

    private void DrawObservatory(ObservatoryDrawing drawing, bool first)
    {
        var canvas = _obsCanvas!;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = 0;
        foreach (var n in drawing.Nodes)
        {
            present.Add(n.Domain.Host);
            var brush = NodeBrush(n.Domain.Color);
            var size = n.Radius * 2;
            if (!_obsVisuals.TryGetValue(n.Domain.Host, out var visual))
            {
                var scale = new ScaleTransform(0, 0);
                var node = new Ellipse
                {
                    Width = size, Height = size, Fill = brush, Cursor = Cursors.Hand, RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = scale, Stroke = ObsRes("SurfaceRaisedBrush"), StrokeThickness = 2, Tag = n.Domain.Host,
                    ToolTip = n.Domain.Host,
                };
                Panel.SetZIndex(node, 3);
                var host = n.Domain.Host;
                node.MouseEnter += (_, _) => ShowObservatoryDetail(host);
                node.MouseLeave += (_, _) => ShowObservatoryDetail(_obsSelected);
                node.MouseLeftButtonDown += (_, e) => { SelectObservatoryDomain(host); e.Handled = true; };
                var line = new Line { X1 = drawing.Center, Y1 = drawing.Center, Stroke = brush, StrokeThickness = 1, Opacity = 0.28, IsHitTestVisible = false };
                Panel.SetZIndex(line, 1);
                canvas.Children.Add(line);
                canvas.Children.Add(node);
                visual = new ObservatoryVisual(node, line, scale);
                _obsVisuals[host] = visual;
                // Entrada: cada nó cresce a partir do centro do seu lugar, em cascata na primeira abertura.
                var delay = TimeSpan.FromMilliseconds(first ? Math.Min(order * 18, 600) : 0);
                var grow = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { BeginTime = delay, EasingFunction = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut } };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                line.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.28, TimeSpan.FromMilliseconds(320)) { BeginTime = delay });
                Canvas.SetLeft(node, n.X - n.Radius);
                Canvas.SetTop(node, n.Y - n.Radius);
                line.X2 = n.X;
                line.Y2 = n.Y;
            }
            else
            {
                // Um domínio já desenhado desliza para o lugar novo (outros domínios chegaram) e pulsa se fez pedidos.
                var duration = TimeSpan.FromMilliseconds(260);
                visual.Node.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(n.X - n.Radius, duration) { EasingFunction = ease });
                visual.Node.BeginAnimation(Canvas.TopProperty, new DoubleAnimation(n.Y - n.Radius, duration) { EasingFunction = ease });
                visual.Node.BeginAnimation(WidthProperty, new DoubleAnimation(size, duration));
                visual.Node.BeginAnimation(HeightProperty, new DoubleAnimation(size, duration));
                visual.Line.BeginAnimation(Line.X2Property, new DoubleAnimation(n.X, duration) { EasingFunction = ease });
                visual.Line.BeginAnimation(Line.Y2Property, new DoubleAnimation(n.Y, duration) { EasingFunction = ease });
                visual.Node.Fill = brush;
                visual.Line.Stroke = brush;
                if (n.Domain.Requests > visual.Requests)
                {
                    var pulse = new DoubleAnimation(1.35, 1, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease };
                    visual.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
                    visual.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
                }
            }
            visual.Requests = n.Domain.Requests;
            visual.Node.StrokeThickness = string.Equals(_obsSelected, n.Domain.Host, StringComparison.OrdinalIgnoreCase) ? 3.5 : 2;
            visual.Node.Stroke = string.Equals(_obsSelected, n.Domain.Host, StringComparison.OrdinalIgnoreCase) ? ObsRes("TextBrush") : ObsRes("SurfaceRaisedBrush");
            AutomationPropertiesName(visual.Node, $"{n.Domain.Host}, {n.Company}: {SiteObservatory.VerdictText(n.Domain)}");
            order++;
        }
        foreach (var gone in _obsVisuals.Keys.Where(k => !present.Contains(k)).ToList())
        {
            canvas.Children.Remove(_obsVisuals[gone].Node);
            canvas.Children.Remove(_obsVisuals[gone].Line);
            _obsVisuals.Remove(gone);
        }

        foreach (var label in _obsLabels) canvas.Children.Remove(label);
        _obsLabels.Clear();
        foreach (var c in drawing.Companies)
        {
            var text = new TextBlock
            {
                Text = c.Name, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = ObsRes("TextMutedBrush"), IsHitTestVisible = false,
                Width = 110, TextTrimming = TextTrimming.CharacterEllipsis,
                // À esquerda do centro o texto encosta à direita (fica junto ao anel), à direita encosta à esquerda.
                TextAlignment = Math.Abs(Math.Cos(c.Angle)) < 0.3 ? TextAlignment.Center : Math.Cos(c.Angle) < 0 ? TextAlignment.Right : TextAlignment.Left,
            };
            var x = Math.Abs(Math.Cos(c.Angle)) < 0.3 ? c.X - 55 : Math.Cos(c.Angle) < 0 ? c.X - 110 : c.X;
            Canvas.SetLeft(text, x);
            Canvas.SetTop(text, c.Y - 8);
            canvas.Children.Add(text);
            _obsLabels.Add(text);
        }
        if (drawing.Nodes.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "Ainda ninguém de fora.\nOs domínios aparecem aqui\nà medida que a página os contacta.",
                FontSize = 12.5, Foreground = ObsRes("TextFaintBrush"), TextAlignment = TextAlignment.Center, Width = 260, IsHitTestVisible = false,
            };
            Canvas.SetLeft(empty, ObservatorySize / 2 - 130);
            Canvas.SetTop(empty, ObservatorySize / 2 + 62);
            canvas.Children.Add(empty);
            _obsLabels.Add(empty);
        }
    }

    private void FillObservatoryList(IReadOnlyList<CompanyGroup> groups)
    {
        var list = _obsList!;
        list.Children.Clear();
        foreach (var g in groups)
        {
            list.Children.Add(new TextBlock
            {
                Text = ObservatoryText.GroupHeader(g), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = ObsRes("TextFaintBrush"),
                Margin = new Thickness(4, list.Children.Count == 0 ? 0 : 10, 0, 3),
            });
            foreach (var d in g.Domains)
            {
                var row = new DockPanel();
                var count = new TextBlock { Text = d.Requests.ToString(), FontSize = 11.5, Foreground = ObsRes("TextMutedBrush"), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(count, Dock.Right);
                row.Children.Add(count);
                row.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = NodeBrush(d.Color), Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(new TextBlock { Text = d.Host, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                var host = d.Host;
                var button = new Button
                {
                    Style = _obsRowStyle, Content = row, Padding = new Thickness(8, 4, 8, 4), Tag = host,
                    Background = string.Equals(_obsSelected, host, StringComparison.OrdinalIgnoreCase) ? ObsRes("SelectedBrush") : Brushes.Transparent,
                    ToolTip = SiteObservatory.VerdictText(d),
                };
                AutomationPropertiesName(button, $"{host}: {d.Requests} pedidos, {SiteObservatory.VerdictText(d)}");
                button.Click += (_, _) => SelectObservatoryDomain(host);
                button.GotKeyboardFocus += (_, _) => ShowObservatoryDetail(host);
                list.Children.Add(button);
            }
        }
    }

    private void SelectObservatoryDomain(string host)
    {
        _obsSelected = host;
        foreach (var (key, visual) in _obsVisuals)
        {
            var selected = string.Equals(key, host, StringComparison.OrdinalIgnoreCase);
            visual.Node.StrokeThickness = selected ? 3.5 : 2;
            visual.Node.Stroke = selected ? ObsRes("TextBrush") : ObsRes("SurfaceRaisedBrush");
        }
        foreach (var button in _obsList!.Children.OfType<Button>())
            button.Background = button.Tag is string tag && string.Equals(tag, host, StringComparison.OrdinalIgnoreCase)
                ? ObsRes("SelectedBrush") : Brushes.Transparent;
        ShowObservatoryDetail(host);
    }

    private void ShowObservatoryDetail(string? host)
    {
        var detail = _obsDetail!;
        detail.Children.Clear();
        var domain = host is null ? null : _obsSnapshot.ThirdParties.FirstOrDefault(d => string.Equals(d.Host, host, StringComparison.OrdinalIgnoreCase));
        if (domain is null)
        {
            detail.Children.Add(new TextBlock
            {
                Text = "Passe o rato por um nó ou escolha um domínio na lista para ver quantos pedidos fez, de que tipo e quem decidiu.",
                FontSize = 12.5, Foreground = ObsRes("TextMutedBrush"), TextWrapping = TextWrapping.Wrap, LineHeight = 18,
            });
            return;
        }
        var owner = CompanyMap.Default.OwnerOf(domain.Host);
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = NodeBrush(domain.Color), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock { Text = domain.Host, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 270 });
        detail.Children.Add(title);
        detail.Children.Add(new TextBlock
        {
            Text = ObservatoryText.DetailLine(domain, owner), FontSize = 12, Foreground = ObsRes("TextMutedBrush"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(18, 3, 0, 0),
        });
        detail.Children.Add(new TextBlock
        {
            Text = SiteObservatory.VerdictText(domain), FontSize = 12.5, Foreground = ObsRes("TextBrush"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(18, 6, 0, 0),
        });

        var site = _obsSnapshot.Site;
        var shieldOn = site is not null && ShieldOn(site);
        var actions = new WrapPanel { Margin = new Thickness(14, 8, 0, 0) };
        // «Porque é que isto passou?»: só para quem fez pedidos permitidos.
        if (domain.Allowed > 0)
        {
            var why = new TextBlock
            {
                Text = SiteObservatory.WhyAllowed(domain, owner, TrackerBlocker.Default.Lists(domain.Host), PrivacyPreferences.ThirdPartyCookiesBlocked),
                FontSize = 12, Foreground = ObsRes("TextMutedBrush"), TextWrapping = TextWrapping.Wrap, LineHeight = 17,
                Margin = new Thickness(18, 6, 0, 0),
                // A explicação aberta sobrevive às atualizações ao vivo do painel.
                Visibility = string.Equals(_obsWhyHost, domain.Host, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed,
            };
            var ask = new Button { Content = "Porque é que isto passou?", Style = (Style)FindResource("PillButton"), Margin = new Thickness(4, 0, 4, 4) };
            var whyHost = domain.Host;
            ask.Click += (_, _) =>
            {
                var open = why.Visibility != Visibility.Visible;
                why.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                _obsWhyHost = open ? whyHost : null;
            };
            actions.Children.Add(ask);
            detail.Children.Add(actions);
            detail.Children.Add(why);
        }
        else detail.Children.Add(actions);
        if (site is null) return;
        var target = domain.Host;
        if (domain.Verdict == ObservedVerdict.UserBlocked)
        {
            var undo = new Button { Content = "Deixar de bloquear", Style = (Style)FindResource("PillButton"), Margin = new Thickness(4, 0, 4, 4) };
            undo.Click += (_, _) => SetSiteRule(site, target, block: false);
            actions.Children.Add(undo);
        }
        else if (shieldOn && domain.Allowed > 0)
        {
            var block = new Button
            {
                Content = "Bloquear neste site", Style = (Style)FindResource("PrimaryPillButton"), Margin = new Thickness(4, 0, 4, 4),
                ToolTip = IsPrivate ? $"Só em {site} e só até terminar a sessão privada" : $"Só em {site}; nos outros sites nada muda",
            };
            block.Click += (_, _) => SetSiteRule(site, target, block: true);
            actions.Children.Add(block);
        }
    }

    // Bloquear (ou deixar de bloquear) um domínio só neste site; a página recarrega para a regra valer já. Deixar de
    // bloquear desfaz a regra que de facto apanha o domínio (também a do domínio-pai) e, numa janela privada, suspende
    // a regra guardada só nesta sessão.
    private void SetSiteRule(string site, string host, bool block)
    {
        _obsSelected = host;
        var isPrivate = IsPrivate;
        // Na pilha de anulação, como as outras ações: o Ctrl+Z desfaz a regra, não um fecho de aba antigo.
        if (block)
        {
            SiteRules.Block(site, host, isPrivate);
            ShowUndoToast(ObservatoryText.RuleToast(site, host, true, isPrivate), $"Bloquear {host} em {site}", () =>
            {
                SiteRules.Unblock(site, host, isPrivate);
                ReloadActive();
            });
        }
        else
        {
            var undone = SiteRules.Unblock(site, host, isPrivate);
            ShowUndoToast(ObservatoryText.UnblockToast(site, host, undone), $"Deixar de bloquear {host} em {site}", () =>
            {
                foreach (var rule in undone.Rules) SiteRules.Block(site, rule, isPrivate);
                ReloadActive();
            });
        }
        ReloadActive();
    }
}

// Textos do painel do Observatório (puros, testados).
public static class ObservatoryText
{
    public static string Stats(ObservatorySnapshot snap, IReadOnlyList<CompanyGroup> groups)
    {
        var domains = snap.ThirdParties.Count;
        if (domains == 0) return snap.FirstPartyRequests == 0 ? "Nenhum pedido registado ainda nesta página." : "Só pedidos ao próprio site — nenhum terceiro contactado.";
        var companies = groups.Count(g => g.Known);
        var blocked = snap.ThirdParties.Count(d => d.Blocked > 0);
        var parts = new List<string>
        {
            domains == 1 ? "1 domínio de terceiros" : $"{domains} domínios de terceiros",
            companies switch { 0 => "nenhuma empresa conhecida", 1 => "1 empresa conhecida", _ => $"{companies} empresas conhecidas" },
            blocked == 1 ? "1 bloqueado" : $"{blocked} bloqueados",
        };
        if (snap.Overflow > 0) parts.Add($"mais {snap.Overflow} só contados");
        return string.Join(" · ", parts);
    }

    public static string RuleToast(string site, string host, bool blocked, bool isPrivate)
        => blocked
            ? $"{host} bloqueado em {site}" + (isPrivate ? " (até terminar a sessão privada)" : string.Empty)
            : $"{host} volta a ser decidido pelas listas em {site}";

    // O que «Deixar de bloquear» fez: a regra que de facto apanhava o domínio (a mais larga) e, numa janela privada,
    // que a regra guardada só fica suspensa nesta sessão.
    public static string UnblockToast(string site, string host, SiteUnblock undone)
    {
        if (undone.Rules.Count == 0) return $"{host} já não estava bloqueado por si em {site}";
        var rule = undone.Rules.MinBy(r => r.Length)!;
        var text = string.Equals(rule, host, StringComparison.OrdinalIgnoreCase)
            ? $"{host} volta a ser decidido pelas listas em {site}"
            : $"{rule} e os subdomínios voltam a ser decididos pelas listas em {site}";
        return undone.SavedKept ? text + " nesta sessão privada (a regra continua nas janelas normais)" : text;
    }

    // Com mais domínios do que cabem no anel: os que ficam de fora estão só na lista.
    public static string? HiddenNote(int hidden)
        => hidden <= 0 ? null : hidden == 1 ? "+1 domínio só na lista" : $"+{hidden} domínios só na lista";

    public static string GroupHeader(CompanyGroup g)
        => g.Name.ToUpperInvariant() + " · " + (g.Domains.Count == 1 ? "1 domínio" : $"{g.Domains.Count} domínios")
           + (g.Blocked > 0 ? " · " + SiteObservatory.BlockedOf(g.Blocked, g.Requests) : g.Requests == 1 ? " · 1 pedido" : $" · {g.Requests} pedidos");

    public static string DetailLine(DomainStat d, string? owner)
        => (owner is null ? "Empresa desconhecida" : owner) + " · "
           + (d.Requests == 1 ? "1 pedido" : $"{d.Requests} pedidos") + " — " + SiteObservatory.KindsText(d);
}
