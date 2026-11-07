using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// Pré-visualização de um grupo ao pairar na etiqueta: um cartão na camada de sobreposição (por cima da página) com as
// miniaturas e os títulos das abas do grupo; clicar numa ativa a aba. A lógica (mosaicos, colunas, posição,
// resumo, tempos) está em Tabs/GroupPreview.cs.
public partial class MainWindow
{
    private Border? _groupPreview;
    private StackPanel? _groupPreviewBody;
    private TabGroup? _groupPreviewGroup;
    private FrameworkElement? _groupPreviewPill;
    private DispatcherTimer? _groupPreviewTimer;
    private bool _overGroupPill;
    private bool _overGroupCard;

    private bool GroupPreviewOpen => _groupPreview is { Visibility: Visibility.Visible };

    private void GroupPill_MouseEnter(object sender, MouseEventArgs e)
    {
        if (GroupOf(sender) is not { } group || _dragging) return;
        _overGroupPill = true;
        _groupPreviewPill = (FrameworkElement)sender;
        // Já aberto para outro grupo: troca na hora, sem esperar outra vez.
        if (GroupPreviewOpen && !ReferenceEquals(_groupPreviewGroup, group))
        {
            ShowGroupPreview(group, _groupPreviewPill);
            return;
        }
        _groupPreviewGroup = group;
        StartGroupPreviewTimer(GroupPreview.ShowDelay, () =>
        {
            if (_overGroupPill && _groupPreviewGroup is { } pending && _groupPreviewPill is { } pill && !_dragging)
                ShowGroupPreview(pending, pill);
        });
    }

    private void GroupPill_MouseLeave(object sender, MouseEventArgs e)
    {
        _overGroupPill = false;
        ScheduleGroupPreviewHide();
    }

    private void ScheduleGroupPreviewHide()
        => StartGroupPreviewTimer(GroupPreview.HideDelay, () =>
        {
            if (!GroupPreview.ShouldStay(_overGroupPill, _overGroupCard)) HideGroupPreview();
        });

    // Um só temporizador: o pedido mais recente (mostrar ou esconder) substitui o anterior.
    private Action? _groupPreviewPending;

    private void StartGroupPreviewTimer(TimeSpan delay, Action action)
    {
        if (_groupPreviewTimer is null)
        {
            _groupPreviewTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
            _groupPreviewTimer.Tick += (_, _) =>
            {
                _groupPreviewTimer.Stop();
                var pending = _groupPreviewPending;
                _groupPreviewPending = null;
                pending?.Invoke();
            };
        }
        _groupPreviewTimer.Stop();
        _groupPreviewTimer.Interval = delay;
        _groupPreviewPending = action;
        _groupPreviewTimer.Start();
    }

    private void HideGroupPreview()
    {
        _groupPreviewTimer?.Stop();
        _overGroupCard = false;
        if (_groupPreview is null) return;
        _groupPreview.Visibility = Visibility.Collapsed;
        _groupPreviewGroup = null;
    }

    private void ShowGroupPreview(TabGroup group, FrameworkElement pill)
    {
        if (!pill.IsDescendantOf(RootBorder)) return;
        SetUpOverlayLayer();
        _groupPreview ??= BuildGroupPreviewCard();
        _groupPreviewGroup = group;
        FillGroupPreview(group);

        var count = Math.Min(group.Members(Tabs).Count(), GroupPreview.MaxTiles);
        var width = GroupPreview.CardWidth(count);
        var left = pill.TransformToAncestor(RootBorder).Transform(new Point(0, 0)).X;
        _groupPreview.Width = width;
        _groupPreview.Margin = new Thickness(GroupPreview.Left(left, pill.ActualWidth, width, RootBorder.ActualWidth), 6, 0, 0);
        if (!GroupPreviewOpen)
        {
            _groupPreview.Visibility = Visibility.Visible;
            _groupPreview.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
            if (_groupPreview.RenderTransform is TranslateTransform slide)
                slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(160))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }
    }

    private Border BuildGroupPreviewCard()
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(GroupPreview.TilePadding),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed, RenderTransform = new TranslateTransform(),
            Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 6, Direction = 270, Opacity = 0.45, Color = Colors.Black },
        };
        card.SetResourceReference(BackgroundProperty, "SurfaceRaisedBrush");
        card.SetResourceReference(BorderBrushProperty, "FieldBorderBrush");
        Grid.SetRow(card, 2);
        Panel.SetZIndex(card, 60);
        WindowChrome.SetIsHitTestVisibleInChrome(card, true);
        card.MouseEnter += (_, _) =>
        {
            _overGroupCard = true;
            _groupPreviewTimer?.Stop();
        };
        card.MouseLeave += (_, _) =>
        {
            _overGroupCard = false;
            ScheduleGroupPreviewHide();
        };
        _groupPreviewBody = new StackPanel();
        card.Child = _groupPreviewBody;
        AddToOverlay(card);
        AutomationProperties.SetName(card, "Abas do grupo");
        return card;
    }

    private void FillGroupPreview(TabGroup group)
    {
        var body = _groupPreviewBody!;
        body.Children.Clear();

        var header = new DockPanel { Margin = new Thickness(6, 2, 6, 8) };
        header.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = group.ColorBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var summary = new TextBlock { Text = GroupPreview.Summary(group, Tabs.Tabs), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        summary.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        DockPanel.SetDock(summary, Dock.Right);
        header.Children.Add(summary);
        var name = new TextBlock { Text = group.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        header.Children.Add(name);
        body.Children.Add(header);

        var tiles = GroupPreview.Tiles(group, Tabs.Tabs, Tabs.Active);
        var grid = new UniformGrid { Columns = GroupPreview.Columns(tiles.Count) };
        foreach (var tile in tiles) grid.Children.Add(BuildGroupPreviewTile(tile));
        body.Children.Add(grid);

        // O cartão substitui a dica da etiqueta: a mesma ajuda, sem uma segunda caixa por cima.
        var hint = new TextBlock
        {
            Text = "Clique numa aba para a abrir · clique na etiqueta para encolher ou expandir · botão direito para o nome e a cor",
            FontSize = 10.5, Margin = new Thickness(6, 6, 6, 2), TextWrapping = TextWrapping.Wrap,
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFaintBrush");
        body.Children.Add(hint);
    }

    private Border BuildGroupPreviewTile(GroupPreviewTile tile)
    {
        var root = new Border
        {
            Width = GroupPreview.TileWidth, Margin = new Thickness(GroupPreview.TilePadding / 2), CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1.5), Cursor = Cursors.Hand, Background = Brushes.Transparent, ClipToBounds = true,
        };
        root.BorderBrush = tile.IsActive ? (Brush)FindResource("IrisBrush") : Brushes.Transparent;
        AutomationProperties.SetName(root, $"Ir para «{tile.Title}»");

        var stack = new StackPanel();
        var thumb = new Border { Height = 84, CornerRadius = new CornerRadius(8, 8, 0, 0), ClipToBounds = true };
        if (tile.Thumbnail is { } image)
            thumb.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top };
        else
        {
            // Sem miniatura (aba a dormir ou ainda a carregar): o monograma do site ocupa o lugar.
            thumb.SetResourceReference(BackgroundProperty, "HoverOverlayBrush");
            thumb.Child = new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(9), Background = tile.Tab.MonogramBrush,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = tile.Tab.Monogram ?? "É", FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
        }
        stack.Children.Add(thumb);

        var row = new DockPanel { Margin = new Thickness(8, 6, 8, 7) };
        if (tile.Tab.Favicon is { } favicon)
            row.Children.Add(new Image { Source = favicon, Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        var title = new TextBlock { Text = tile.Title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, tile.IsActive ? "TextBrush" : "TextMutedBrush");
        row.Children.Add(title);
        stack.Children.Add(row);
        root.Child = stack;

        root.MouseEnter += (_, _) => root.SetResourceReference(BackgroundProperty, "HoverOverlayBrush");
        root.MouseLeave += (_, _) => root.Background = Brushes.Transparent;
        root.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var tab = tile.Tab;
            HideGroupPreview();
            if (Tabs.Tabs.Contains(tab)) Tabs.Active = tab;
            ReturnFocusToPage();
        };
        return root;
    }
}
