using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using CefSharp;
using Eteris.Browser.Browser;
using Eteris.Browser.Palette;

namespace Eteris.Browser;

public partial class MainWindow
{
    private Popup? _browserMenu;
    private List<Button> _browserMenuButtons = [];

    private bool BrowserMenuOpen => _browserMenu is { IsOpen: true };

    private void ToggleBrowserMenu()
    {
        if (_browserMenu is { IsOpen: true } open)
        {
            open.IsOpen = false;
            MenuButton.Focus();
            return;
        }
        if (!MenuButton.IsVisible)
        {
            // Sem barra de ferramentas (modo foco ou ecrã inteiro) não há onde ancorar o menu: diz-se porquê.
            ShowToast(_pageFullscreen ? "O menu não está disponível em ecrã inteiro — Esc para sair" : "O menu não está disponível em modo foco — F11 para sair");
            return;
        }

        ClosePalette();
        CloseRadar();
        CloseShortcuts();
        CloseSuggestions();
        ShieldPopup.IsOpen = false;
        MediaPopup.IsOpen = false;
        DownloadsPopup.IsOpen = false;
        SiteInfoPopup.IsOpen = false;

        var popup = new Popup
        {
            PlacementTarget = MenuButton,
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = BrowserMenuPlacement,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            StaysOpen = false,
        };
        var buttons = new List<Button>();
        popup.Child = BuildBrowserMenu(popup, buttons);
        // As teclas premidas no menu não ficam nele: o Popup não tem pai, por isso encaminha-as pelo MenuButton até à
        // janela, e é Window_PreviewKeyDown que as entrega a HandleBrowserMenuKey.
        popup.Opened += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            if (popup.IsOpen) FocusBrowserMenuButton(buttons[0]);
        }, DispatcherPriority.Input);
        popup.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_browserMenu, popup)) return;
            _browserMenu = null;
            _browserMenuButtons = [];
        };
        _browserMenu = popup;
        _browserMenuButtons = buttons;
        popup.IsOpen = true;
    }

    private void CloseBrowserMenu()
    {
        if (_browserMenu is { IsOpen: true } open) open.IsOpen = false;
    }

    private static CustomPopupPlacement[] BrowserMenuPlacement(Size popup, Size target, Point offset) =>
    [
        new(new Point(target.Width - popup.Width, target.Height + 6), PopupPrimaryAxis.Vertical),
        new(new Point(target.Width - popup.Width, -popup.Height - 6), PopupPrimaryAxis.Vertical),
    ];

    private Border BuildBrowserMenu(Popup popup, List<Button> buttons)
    {
        var theme = new ResourceDictionary { Source = new Uri("pack://application:,,,/Eteris.Browser;component/Theme/BrowserMenu.xaml", UriKind.Absolute) };
        var rowStyle = (Style)theme["BrowserMenuRow"];
        var controlStyle = (Style)theme["BrowserMenuControl"];
        var items = new StackPanel { Margin = new Thickness(6, 0, 6, 6) };

        void Row(string glyph, string label, Action action, string? shortcut = null, bool enabled = true)
        {
            var button = new Button
            {
                Style = rowStyle,
                Content = BrowserMenuLabel(glyph, label, shortcut),
                IsEnabled = enabled,
            };
            AutomationProperties.SetName(button, label);
            if (shortcut is not null) AutomationProperties.SetAcceleratorKey(button, shortcut);
            button.Click += (_, _) => RunBrowserMenuAction(popup, action);
            buttons.Add(button);
            items.Children.Add(button);
        }

        void Divider() => items.Children.Add(BrowserMenuDivider());

        Row("\uE710", "Nova aba", OpenNewTab, "Ctrl+T");
        Row("\uE8A7", "Nova janela", () => OpenWindow(isPrivate: false), "Ctrl+N");
        Row("\uE72E", "Nova janela privada", () => OpenWindow(isPrivate: true), "Ctrl+Shift+N");
        if (PrivateWindows().Count > 0) Row("\uE711", "Terminar sessão privada", OfferEndPrivateSession);
        if (Tabs.RecentlyClosed.Count > 0)
            Row("\uE7A7", "Reabrir aba fechada", () => Tabs.ReopenClosed(), "Ctrl+Shift+T");

        Divider();
        if (!IsPrivate) Row("\uE81C", "Histórico", OpenHistory, "Ctrl+H");
        Row("\uE734", "Favoritos", OpenBookmarks, "Ctrl+Shift+O");
        Row("\uE896", "Transferências", ToggleDownloads, "Ctrl+J");
        Row("\uE71D", "Radar de abas", ToggleRadar, "Ctrl+Shift+A");
        Row("\uE8A0", SidebarOpen ? "Fechar a barra lateral" : "Barra lateral", () => ToggleSidebar(), "Ctrl+Shift+B");

        Divider();
        items.Children.Add(BuildBrowserMenuZoom(popup, buttons, controlStyle));

        Divider();
        Row("\uE721", "Procurar na página…", OpenFind, "Ctrl+F", ActiveBrowser is not null);
        Row("\uE749", "Imprimir…", () => ActiveBrowser?.Print(), "Ctrl+P", ActiveBrowser is not null);
        Row("\uE722", "Capturar página inteira", () => CapturePage(true), "Ctrl+Shift+S", ActiveBrowser is not null);
        if (!IsPrivate)
            Row("\uE74D", "Apagar dados de navegação…", () => OpenInternal(SettingsPage.Url + "#clearToggle"));

        Divider();
        Row("\uE713", "Definições", OpenSettings, "Ctrl+,");
        Row("\uE756", "Paleta de comandos", OpenPalette, "Ctrl+K");
        Row("\uE765", "Atalhos de teclado", ToggleShortcuts, "F1");

        Divider();
        Row("\uE8BB", "Fechar janela", Close, "Alt+F4");

        var body = new ScrollViewer
        {
            Content = items,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        var content = new DockPanel();
        var heading = BuildBrowserMenuHeading();
        DockPanel.SetDock(heading, Dock.Top);
        content.Children.Add(heading);
        content.Children.Add(body);
        KeyboardNavigation.SetTabNavigation(content, KeyboardNavigationMode.Cycle);

        var frame = new Border
        {
            Child = content,
            Width = 332,
            MaxHeight = Math.Clamp(ActualHeight - 74, 254, 665),
            CornerRadius = new CornerRadius(13),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            SnapsToDevicePixels = true,
            Margin = new Thickness(0, 0, 1, 1),
        };
        frame.Resources.MergedDictionaries.Add(theme);
        frame.SetResourceReference(Border.BackgroundProperty, "SurfaceRaisedBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "FieldBorderBrush");
        AutomationProperties.SetName(frame, "Menu do Éteris");
        return frame;
    }

    private Border BuildBrowserMenuHeading()
    {
        var title = new TextBlock { Text = "Éteris", FontSize = 17, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var label = new TextBlock
        {
            Text = IsPrivate ? "JANELA PRIVADA" : "MENU DO NAVEGADOR",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 2, 0, 0),
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(label);
        var iris = new Ellipse
        {
            Width = 24, Height = 24, StrokeThickness = 3,
            Margin = new Thickness(0, 0, 11, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        iris.SetResourceReference(Shape.StrokeProperty, "IrisBrush");
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(iris);
        line.Children.Add(text);

        var heading = new Border
        {
            Child = line,
            Padding = new Thickness(15, 11, 12, 10),
            Margin = new Thickness(0, 0, 0, 5),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        heading.SetResourceReference(Border.BorderBrushProperty, "FieldBorderBrush");
        heading.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        return heading;
    }

    private DockPanel BuildBrowserMenuZoom(Popup popup, List<Button> buttons, Style style)
    {
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        var canZoom = ActiveBrowser is not null;
        string Percent() => $"{ZoomSteps.Percent(ActiveBrowser?.ZoomLevel ?? 0)}%";

        Button Control(string text, string label, bool enabled, double width)
        {
            var button = new Button { Style = style, Content = text, Width = width, IsEnabled = enabled, ToolTip = label };
            AutomationProperties.SetName(button, label);
            buttons.Add(button);
            controls.Children.Add(button);
            return button;
        }

        var decrease = Control("−", "Reduzir zoom", canZoom, 31);
        var reset = Control(Percent(), "Repor zoom para 100%", canZoom, 54);
        var increase = Control("+", "Aumentar zoom", canZoom, 31);
        var fullscreen = Control("\uE740", "Ecrã inteiro (Esc para sair)", Tabs.Active is not null, 33);
        fullscreen.Margin = new Thickness(8, 0, 0, 0);
        fullscreen.SetResourceReference(FontFamilyProperty, "IconFont");

        void Adjust(int direction)
        {
            Zoom(direction);
            reset.Content = Percent();
            AutomationProperties.SetName(reset, $"Zoom atual {Percent()}. Repor para 100%");
        }

        decrease.Click += (_, _) => Adjust(-1);
        reset.Click += (_, _) => Adjust(0);
        increase.Click += (_, _) => Adjust(1);
        fullscreen.Click += (_, _) => RunBrowserMenuAction(popup, ToggleWindowFullscreen);

        var row = new DockPanel { Margin = new Thickness(9, 3, 7, 3), MinHeight = 34 };
        DockPanel.SetDock(controls, Dock.Right);
        row.Children.Add(controls);
        row.Children.Add(BrowserMenuLabel("\uE71E", "Zoom"));
        return row;
    }

    private static Grid BrowserMenuLabel(string glyph, string title, string? shortcut = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock { Text = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        icon.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        grid.Children.Add(icon);

        var label = new TextBlock { Text = title, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        if (shortcut is not null)
        {
            var key = new TextBlock
            {
                Text = shortcut, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0),
            };
            key.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            Grid.SetColumn(key, 2);
            grid.Children.Add(key);
        }
        return grid;
    }

    private static Border BrowserMenuDivider()
    {
        var divider = new Border { Height = 1, Margin = new Thickness(8, 4, 8, 4) };
        divider.SetResourceReference(Border.BackgroundProperty, "FieldBorderBrush");
        return divider;
    }


    private void RunBrowserMenuAction(Popup popup, Action action)
    {
        popup.IsOpen = false;
        ReturnFocusToPage();
        action();
    }

    // Tecla premida com o menu aberto (vem de Window_PreviewKeyDown, como as do radar). O Esc fecha o menu — não para
    // o carregamento — e um atalho fecha-o antes de abrir o que quer que abra (paleta, radar, transferências, aba…).
    private bool HandleBrowserMenuKey(Key key, bool ctrl, bool shift, bool alt)
    {
        if (_browserMenu is not { IsOpen: true } popup) return false;
        if (key == Key.Escape || (alt && !ctrl && !shift && key == Key.F))
        {
            popup.IsOpen = false;
            MenuButton.Focus();
            return true;
        }
        if (!ctrl && !shift && !alt && key is Key.Up or Key.Down or Key.Home or Key.End or Key.Left or Key.Right)
        {
            var enabled = _browserMenuButtons.Where(button => button.IsEnabled).ToArray();
            if (enabled.Length == 0) return true;
            var current = Array.FindIndex(enabled, button => button.IsKeyboardFocusWithin);
            var next = key switch
            {
                Key.Home => 0,
                Key.End => enabled.Length - 1,
                Key.Up or Key.Left => current <= 0 ? enabled.Length - 1 : current - 1,
                _ => (current + 1) % enabled.Length,
            };
            FocusBrowserMenuButton(enabled[next]);
            return true;
        }
        if (Shortcut(key, ctrl, shift, alt) is { } action)
        {
            RunBrowserMenuAction(popup, action);
            return true;
        }
        return false;
    }

    private static void FocusBrowserMenuButton(Button button)
    {
        button.Focus();
        button.BringIntoView();
    }
}
