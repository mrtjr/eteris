using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// Etiqueta dos grupos de abas: clique encolhe/expande; botão direito dá o menu do grupo.
public partial class MainWindow
{
    private const double GroupPillWidth = 90;

    private static TabGroup? GroupOf(object sender) => ((sender as FrameworkElement)?.DataContext as BrowserTab)?.Group;

    private void GroupPill_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Tratado aqui para a aba não ser selecionada nem arrastada.
        e.Handled = true;
        HideGroupPreview();
        if (GroupOf(sender) is { } group) Tabs.ToggleCollapse(group);
    }

    private void GroupPill_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        HideGroupPreview();
        if (GroupOf(sender) is not { } group) return;
        var pill = (FrameworkElement)sender;
        new DarkMenu()
            .Item("", "Mudar o nome…", () => RenameGroup(group, pill))
            .Separator()
            .With(menu =>
            {
                for (var i = 0; i < TabGroup.Palette.Length; i++)
                {
                    var index = i;
                    var check = index == group.ColorIndex ? "  ✓" : string.Empty;
                    menu.Item("", TabGroup.PaletteNames[i] + check, () => group.ColorIndex = index,
                        iconBrush: (Brush)new BrushConverter().ConvertFromString(TabGroup.Palette[i])!);
                }
            })
            .Separator()
            .Item(group.IsCollapsed ? "" : "", group.IsCollapsed ? "Expandir o grupo" : "Encolher o grupo", () => Tabs.ToggleCollapse(group))
            .Item("", "Guardar grupo como sessão", () => SaveGroupAsSession(group), enabled: !IsPrivate)
            .Item("", "Desagrupar", () => Tabs.UngroupAll(group))
            .Item("", "Fechar o grupo", () => Tabs.CloseGroup(group))
            .Open(pill);
    }

    // Caixa de texto por cima da etiqueta: Enter guarda, Esc ou clicar fora cancela.
    private void RenameGroup(TabGroup group, FrameworkElement anchor)
    {
        var box = new TextBox
        {
            Text = group.Name, MinWidth = 180, FontSize = 13, Padding = new Thickness(8, 5, 8, 5),
            Foreground = (Brush)FindResource("TextBrush"), Background = (Brush)FindResource("SurfaceRaisedBrush"),
            BorderBrush = group.ColorBrush, BorderThickness = new Thickness(1.5), CaretBrush = (Brush)FindResource("TextBrush"),
        };
        var popup = new Popup
        {
            PlacementTarget = anchor, Placement = PlacementMode.Bottom, VerticalOffset = 4, StaysOpen = false,
            AllowsTransparency = true, Child = new Border { CornerRadius = new CornerRadius(8), Child = box },
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && box.Text.Trim().Length > 0)
            {
                group.Name = box.Text.Trim();
                popup.IsOpen = false;
            }
            else if (e.Key == Key.Escape) popup.IsOpen = false;
        };
        popup.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        popup.IsOpen = true;
    }

    // Abas escondidas por grupos encolhidos não ocupam lugar; cada etiqueta ocupa o seu.
    private (double Reserved, int Visible) GroupLayout()
    {
        var heads = Tabs.Tabs.Count(t => t.IsGroupHead);
        var collapsedHeads = Tabs.Tabs.Count(t => t.IsGroupHead && t.Group is { IsCollapsed: true });
        var visible = Tabs.Tabs.Count(t => !t.IsPinned && !t.IsHiddenByGroup) - collapsedHeads;
        return (heads * GroupPillWidth, Math.Max(visible, 1));
    }

    // O grupo fica guardado como sessão com o nome do grupo (guardar de novo atualiza-a).
    private void SaveGroupAsSession(TabGroup group)
    {
        var tabs = group.Members(Tabs).Where(t => !Browser.NewTabPage.IsNewTab(t.Address))
            .Select(t => new Data.SavedTab(t.Address, t.Title, t.CustomTitle, t.Note)).ToList();
        if (tabs.Count == 0) return;
        Data.AppData.SaveSession(group.Name, tabs);
        ShowToast($"Grupo «{group.Name}» guardado como sessão ({(tabs.Count == 1 ? "1 aba" : $"{tabs.Count} abas")})");
    }
}
