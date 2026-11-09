using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using Eteris.Browser.Palette;

namespace Eteris.Browser;

// Folha de atalhos (F1 · Ctrl+/): todos os atalhos numa só vista, agrupados.
public partial class MainWindow
{
    private Grid? _shortcuts;

    private bool ShortcutsOpen => _shortcuts is { Visibility: Visibility.Visible };

    private void ToggleShortcuts()
    {
        if (ShortcutsOpen) CloseShortcuts();
        else OpenShortcuts();
    }

    private void OpenShortcuts()
    {

        CloseBrowserMenu();
        CloseRadar();
        ClosePalette();
        CloseSuggestions();
        _shortcuts ??= BuildShortcuts();
        _shortcuts.Visibility = Visibility.Visible;
        BrowserHost.Effect = new BlurEffect { Radius = 22, RenderingBias = RenderingBias.Performance };
        var card = (FrameworkElement)_shortcuts.Children[1];
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        var scale = (ScaleTransform)card.RenderTransform;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        // Aberta com o foco na página, as teclas iam para o Chromium: as setas e o Espaço rolavam a página por baixo e o
        // Esc chegava-lhe também. A camada ativa-se e a lista da folha fica com o teclado (as setas rolam-na a ela).
        if (card is Border { Child: UIElement list }) FocusInOverlay(list);
    }

    // As cores são lidas quando a folha é construída: com outro tema, constrói-se de novo.
    private void ResetShortcutsForTheme()
    {
        if (_shortcuts is null) return;
        CloseShortcuts();
        OverlayRoot.Children.Remove(_shortcuts);
        _shortcuts = null;
    }

    private void CloseShortcuts()
    {
        if (!ShortcutsOpen) return;
        _shortcuts!.Visibility = Visibility.Collapsed;
        BrowserHost.Effect = null;
        ReturnFocusToPage();
    }

    private Grid BuildShortcuts()
    {
        Brush Res(string key) => (Brush)FindResource(key);
        var overlay = new Grid { Visibility = Visibility.Collapsed };
        Grid.SetRowSpan(overlay, 3);
        Panel.SetZIndex(overlay, 110);
        WindowChrome.SetIsHitTestVisibleInChrome(overlay, true);

        var backdrop = new System.Windows.Shapes.Rectangle { Fill = Res("ScrimBrush") };
        backdrop.MouseLeftButtonDown += (_, _) => CloseShortcuts();
        overlay.Children.Add(backdrop);

        var columns = new UniformGrid { Columns = 3, Margin = new Thickness(0, 18, 0, 0) };
        foreach (var group in ShortcutSheet.Groups)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 28, 22) };
            panel.Children.Add(new TextBlock
            {
                Text = group.Title.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = Res("TextFaintBrush"), Margin = new Thickness(2, 0, 0, 8),
            });
            foreach (var item in group.Items)
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3), LastChildFill = true };
                var keys = new Border
                {
                    CornerRadius = new CornerRadius(7), Background = Res("HoverBrush"), Padding = new Thickness(8, 3, 8, 3),
                    BorderBrush = Res("PressedBrush"), BorderThickness = new Thickness(0, 0, 0, 1.5),
                    Child = new TextBlock { Text = item.Keys, FontSize = 11.5, Foreground = Res("TextBrush") },
                };
                DockPanel.SetDock(keys, Dock.Right);
                row.Children.Add(keys);
                row.Children.Add(new TextBlock
                {
                    Text = item.Action, FontSize = 13, Foreground = Res("TextMutedBrush"),
                    VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 10, 0),
                });
                panel.Children.Add(row);
            }
            columns.Children.Add(panel);
        }

        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 20, Foreground = Res("IrisBrush"), VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock { Text = "Atalhos de teclado", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock { Text = "Esc para fechar · Ctrl+K para a paleta", FontSize = 12.5, Foreground = Res("TextFaintBrush"), Margin = new Thickness(16, 4, 0, 0), VerticalAlignment = VerticalAlignment.Center });

        var content = new StackPanel();
        content.Children.Add(title);
        content.Children.Add(columns);

        // A lista recebe o teclado ao abrir (FocusInOverlay): com nome, o leitor de ecrã diz «Atalhos de teclado» e não
        // só um painel de deslocamento.
        var list = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content, Focusable = true, FocusVisualStyle = null };
        AutomationProperties.SetName(list, "Atalhos de teclado");
        AutomationProperties.SetHelpText(list, "Esc para fechar");
        var card = new Border
        {
            MaxWidth = 1000, Margin = new Thickness(40, 70, 40, 40), VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(22), Padding = new Thickness(34, 28, 10, 8),
            Background = Res("SurfaceRaisedBrush"), BorderBrush = Res("IrisBrush"), BorderThickness = new Thickness(1.5),
            Effect = new DropShadowEffect { BlurRadius = 60, ShadowDepth = 18, Direction = 270, Opacity = 0.6, Color = Colors.Black },
            RenderTransformOrigin = new Point(0.5, 0), RenderTransform = new ScaleTransform(),
            Child = list,
        };
        overlay.Children.Add(card);
        AddToOverlay(overlay);
        return overlay;
    }
}
