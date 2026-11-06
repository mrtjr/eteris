using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Eteris.Browser.Infrastructure;

// Menus de contexto escuros construídos em código (abas, página…), com o tema do Éteris.
public sealed class DarkMenu
{
    private readonly ContextMenu _menu = new();

    public DarkMenu() => _menu.Style = (Style)Application.Current.FindResource("DarkContextMenu");

    public DarkMenu Item(string glyph, string header, Action action, string? gesture = null, bool enabled = true, Brush? iconBrush = null)
    {
        var item = new MenuItem
        {
            Style = (Style)Application.Current.FindResource("DarkMenuItem"),
            Header = header,
            InputGestureText = gesture ?? string.Empty,
            IsEnabled = enabled,
            Icon = new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
                FontSize = 13,
                Foreground = iconBrush ?? (Brush)Application.Current.FindResource("TextMutedBrush"),
            },
        };
        item.Click += (_, _) => action();
        _menu.Items.Add(item);
        return this;
    }

    // Para juntar itens calculados (ex.: um por grupo de abas) sem partir a cadeia fluente.
    public DarkMenu With(Action<DarkMenu> add)
    {
        add(this);
        return this;
    }

    public DarkMenu Separator()
    {
        _menu.Items.Add(new Separator { Style = (Style)Application.Current.FindResource("DarkSeparator") });
        return this;
    }

    public void Open(UIElement target)
    {
        _menu.PlacementTarget = target;
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }
}
