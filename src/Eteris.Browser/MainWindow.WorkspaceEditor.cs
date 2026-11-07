using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// Editor de um Espaço: uma caixa na camada de sobreposição por baixo do chip (ou da barra de endereço, vindo da
// paleta) com o nome, o seletor de emoji e as cores da paleta dos grupos — os mesmos seletores das abas
// (MainWindow.TabIdentity.cs). Emoji e cor aplicam-se logo ao clicar (vê-se no chip); o nome guarda-se com Enter.
// Partilha a camada «_renameLayer» com a caixa das abas: só uma das duas está aberta, e Esc fecha-a.
public partial class MainWindow
{
    private void EditWorkspace(Workspace workspace, UIElement anchor, bool isNew = false)
    {
        if (!Tabs.Workspaces.Contains(workspace)) return;
        CloseRename();
        Brush Res(string key) => (Brush)FindResource(key);

        var box = new TextBox
        {
            Text = workspace.Name, MinWidth = 260, FontSize = 13, Padding = new Thickness(8, 5, 8, 5), MaxLength = Workspace.MaxNameLength,
            Foreground = Res("TextBrush"), Background = Res("SurfaceBrush"), BorderBrush = Res("FieldBorderBrush"),
            BorderThickness = new Thickness(1), CaretBrush = Res("TextBrush"),
        };
        AutomationProperties.SetName(box, "Nome do Espaço");
        var hint = new TextBlock
        {
            Text = isNew ? "Pessoal, Trabalho, Estudo… as abas deste Espaço ficam separadas das dos outros" : Workspace.CountText(workspace.TabCount),
            FontSize = 11, Foreground = Res("TextFaintBrush"), Margin = new Thickness(2, 4, 2, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 310,
        };
        var emojis = BuildEmojiPicker(() => workspace.Icon, icon => workspace.Icon = icon);
        var colors = BuildColorPicker(() => workspace.ColorIndex, color => workspace.ColorIndex = color ?? workspace.ColorIndex, allowNone: false);

        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var delete = new Button
        {
            Content = "Apagar", Style = (Style)FindResource("PillButton"), IsEnabled = Tabs.HasWorkspaces,
            ToolTip = Tabs.HasWorkspaces ? "As abas deste Espaço vão para as fechadas recentes; as trancadas passam para o vizinho" : "O último Espaço não se apaga",
        };
        var save = new Button { Content = "Guardar", Style = (Style)FindResource("PrimaryPillButton"), ToolTip = "Enter", Margin = new Thickness(8, 0, 0, 0) };
        var keys = new TextBlock { Text = "Enter guarda · Esc fecha", FontSize = 11, Foreground = Res("TextFaintBrush"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(delete, Dock.Left);
        DockPanel.SetDock(save, Dock.Right);
        footer.Children.Add(delete);
        footer.Children.Add(save);
        footer.Children.Add(keys);

        var panel = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        panel.Children.Add(new TextBlock { Text = isNew ? "Novo Espaço" : "Nome do Espaço", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Res("TextFaintBrush"), Margin = new Thickness(2, 0, 0, 6) });
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
        AutomationProperties.SetName(card, "Nome, emoji e cor do Espaço");

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

        // Por baixo do chip (ou da barra de endereço), sem sair da janela.
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
            if (!ReferenceEquals(_renameLayer, layer)) return;
            RemoveFromOverlay(layer);
            _renameLayer = null;
            ScheduleSessionSave();
            ReturnFocusToPage();
        }
        void Commit()
        {
            workspace.Rename(box.Text, icon: workspace.Icon);
            Close();
        }
        veil.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            Close();
        };
        save.Click += (_, _) => Commit();
        delete.Click += (_, _) =>
        {
            Close();
            DeleteWorkspace(workspace);
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
}
