using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// «Mudar o nome…» de uma aba: uma caixa por baixo da aba com o nome próprio, um seletor de emoji e as cores da
// paleta dos grupos para o ícone. Emoji e cor aplicam-se logo ao clicar (vê-se na faixa); o nome, com Enter.
// As regras (normalização, o que sobrevive a navegações) estão em Tabs/TabIdentity.cs.
public partial class MainWindow
{
    // O WPF desenha os emojis a uma só cor; a fonte de emoji do Windows garante que todos têm glifo.
    private FontFamily EmojiFont => (FontFamily)FindResource("EmojiFont");

    private void RenameTab(BrowserTab tab, UIElement anchor)
    {
        if (!Tabs.Tabs.Contains(tab)) return;
        CloseRename();
        Brush Res(string key) => (Brush)FindResource(key);

        var box = new TextBox
        {
            Text = tab.CustomTitle ?? string.Empty, MinWidth = 260, FontSize = 13, Padding = new Thickness(8, 5, 8, 5),
            Foreground = Res("TextBrush"), Background = Res("SurfaceBrush"), BorderBrush = Res("FieldBorderBrush"),
            BorderThickness = new Thickness(1), CaretBrush = Res("TextBrush"), MaxLength = TabIdentity.MaxNameLength,
        };
        AutomationProperties.SetName(box, "Nome da aba");
        var hint = new TextBlock
        {
            Text = tab.Title, FontSize = 11, Foreground = Res("TextFaintBrush"), Margin = new Thickness(2, 4, 2, 0),
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 300,
        };

        // Emojis: um botão por emoji; o escolhido fica com o contorno íris.
        var emojis = new WrapPanel { Margin = new Thickness(0, 10, 0, 0), MaxWidth = 310 };
        var emojiButtons = new List<Border>();
        void RefreshEmojis()
        {
            foreach (var button in emojiButtons)
                button.BorderBrush = (string?)button.Tag == tab.Emoji ? Res("IrisBrush") : Brushes.Transparent;
        }
        foreach (var emoji in TabIdentity.Emojis)
        {
            var button = new Border
            {
                Width = 32, Height = 32, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1.5), Margin = new Thickness(2),
                Background = Brushes.Transparent, Cursor = Cursors.Hand, Tag = emoji,
                Child = new TextBlock
                {
                    Text = emoji, FontSize = 16, FontFamily = EmojiFont, Foreground = Res("TextBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            AutomationProperties.SetName(button, $"Emoji {emoji}");
            button.MouseEnter += (_, _) => button.Background = Res("HoverOverlayBrush");
            button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
            button.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                // Clicar no emoji já escolhido tira-o.
                tab.Emoji = tab.Emoji == emoji ? null : emoji;
                RefreshEmojis();
            };
            emojiButtons.Add(button);
            emojis.Children.Add(button);
        }

        // Cores: as da paleta dos grupos, mais «sem cor».
        var colors = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var colorDots = new List<Border>();
        void RefreshColors()
        {
            foreach (var dot in colorDots)
                dot.BorderBrush = Equals(dot.Tag, tab.ColorIndex) ? Res("TextBrush") : Brushes.Transparent;
        }
        for (var i = -1; i < TabGroup.Palette.Length; i++)
        {
            int? index = i < 0 ? null : i;
            var dot = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(4, 2, 4, 2), BorderThickness = new Thickness(2),
                Background = index is { } c ? TabGroup.BrushAt(c) : Res("FieldBorderBrush"), Cursor = Cursors.Hand, Tag = index,
            };
            if (index is null)
                dot.Child = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 9, Foreground = Res("TextMutedBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(dot, index is { } n ? $"Cor {TabGroup.PaletteNames[n]}" : "Sem cor");
            dot.ToolTip = index is { } t ? TabGroup.PaletteNames[t] : "Sem cor";
            dot.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                tab.ColorIndex = index;
                RefreshColors();
            };
            colorDots.Add(dot);
            colors.Children.Add(dot);
        }
        RefreshEmojis();
        RefreshColors();

        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var reset = new Button { Content = "Repor", Style = (Style)FindResource("PillButton"), ToolTip = "Volta ao título da página, sem emoji nem cor" };
        var save = new Button { Content = "Guardar", Style = (Style)FindResource("PrimaryPillButton"), ToolTip = "Enter", Margin = new Thickness(8, 0, 0, 0) };
        var keys = new TextBlock { Text = "Enter guarda · Esc fecha", FontSize = 11, Foreground = Res("TextFaintBrush"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(reset, Dock.Left);
        DockPanel.SetDock(save, Dock.Right);
        footer.Children.Add(reset);
        footer.Children.Add(save);
        footer.Children.Add(keys);

        var panel = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        var caption = new TextBlock { Text = "Nome da aba", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Res("TextFaintBrush"), Margin = new Thickness(2, 0, 0, 6) };
        panel.Children.Add(caption);
        panel.Children.Add(box);
        panel.Children.Add(hint);
        panel.Children.Add(emojis);
        panel.Children.Add(colors);
        panel.Children.Add(footer);

        var card = new Border
        {
            CornerRadius = new CornerRadius(12), Background = Res("SurfaceRaisedBrush"), BorderBrush = Res("FieldBorderBrush"),
            BorderThickness = new Thickness(1), Child = panel, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 26, ShadowDepth = 6, Direction = 270, Opacity = 0.45, Color = Colors.Black },
        };
        AutomationProperties.SetName(card, "Mudar o nome da aba");

        // A caixa vive na camada de sobreposição (uma Popup aberta a partir do menu da aba perdia o teclado para a
        // página). Um véu quase invisível por baixo apanha o clique fora, que fecha sem guardar o nome.
        var layer = new Grid();
        Grid.SetRowSpan(layer, 3);
        Panel.SetZIndex(layer, 70);
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(layer, true);
        var veil = new Border { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
        layer.Children.Add(veil);
        layer.Children.Add(card);
        SetUpOverlayLayer();
        AddToOverlay(layer);
        _renameLayer = layer;

        // Por baixo da aba (ou da barra de endereço, quando vem da paleta), sem sair da janela.
        var top = TitleRowHeight + ToolbarRowHeight;
        var left = 16.0;
        if (anchor is FrameworkElement element && element.IsDescendantOf(RootBorder))
        {
            var origin = element.TransformToAncestor(RootBorder).Transform(new Point(0, 0));
            left = Math.Max(8, Math.Min(origin.X, RootBorder.ActualWidth - 340));
            top = origin.Y + element.RenderSize.Height;
        }
        card.Margin = new Thickness(left, top + 4, 0, 0);

        void Close()
        {
            if (_renameLayer is null) return;
            RemoveFromOverlay(_renameLayer);
            _renameLayer = null;
            ReturnFocusToPage();
        }
        void Commit()
        {
            tab.CustomTitle = TabIdentity.NormalizeName(box.Text);
            Close();
        }
        veil.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            Close();
        };
        save.Click += (_, _) => Commit();
        reset.Click += (_, _) =>
        {
            tab.SetIdentity(null, null, null);
            Close();
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Commit();
            else if (e.Key == Key.Escape) Close();
            else return;
            e.Handled = true;
        };
        layer.Loaded += (_, _) =>
        {
            FocusInOverlay(box);
            box.SelectAll();
        };
    }

    private Grid? _renameLayer;

    private bool RenameOpen => _renameLayer is not null;

    private void CloseRename()
    {
        if (_renameLayer is null) return;
        RemoveFromOverlay(_renameLayer);
        _renameLayer = null;
    }
}
