using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Eteris.Browser.Infrastructure;

// Menus de contexto escuros construídos em código (abas, página…), com o tema do Éteris.
public sealed class DarkMenu
{
    private readonly ContextMenu _menu = new();

    // Menus abertos agora (a interface que desaparece não esconde as barras com um menu pendurado nelas).
    private static int s_open;
    public static bool AnyOpen => s_open > 0;

    public DarkMenu()
    {
        _menu.Style = (Style)Application.Current.FindResource("DarkContextMenu");
        // Abre muitas vezes por baixo de um botão de ícone, de quem herdaria a fonte de ícones (ver CodePopup).
        CodePopup.WithUiFont(_menu);
        _menu.Opened += (_, _) => s_open++;
        _menu.Closed += (_, _) => s_open = Math.Max(0, s_open - 1);
    }

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

    // Menu de um botão (selo, pílula): abre encostado por baixo dele, não no ponto do rato.
    public void OpenBelow(UIElement target)
    {
        _menu.PlacementTarget = target;
        _menu.Placement = PlacementMode.Bottom;
        _menu.HorizontalOffset = -8;
        _menu.VerticalOffset = 6;
        _menu.IsOpen = true;
    }

    // Menu de um botão encostado a uma margem (os separadores da barra lateral): abre para o lado de dentro.
    public void OpenBeside(UIElement target, bool toLeft)
    {
        _menu.PlacementTarget = target;
        _menu.Placement = toLeft ? PlacementMode.Left : PlacementMode.Right;
        _menu.VerticalOffset = -4;
        _menu.IsOpen = true;
    }

    public void Open(UIElement target)
    {
        _menu.PlacementTarget = target;
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }
}
