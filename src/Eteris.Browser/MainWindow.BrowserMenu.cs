using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using CefSharp;
using Eteris.Browser.Browser;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Palette;

namespace Eteris.Browser;

public partial class MainWindow
{
    private Popup? _browserMenu;
    private List<Button> _browserMenuButtons = [];
    // Quem tinha o teclado quando o menu abriu (a página, a barra de endereço, o «⋯»…): recebe-o de volta ao fechar.
    private BrowserMenuReturn? _browserMenuReturn;
    // Lido na thread do CEF (ShortcutFromPage): o menu está aberto. Atualizado só na thread da janela, ao abrir e ao fechar.
    private volatile bool _browserMenuOpenForPage;

    private sealed record BrowserMenuReturn(IInputElement? Element, nint ChildWindow);

    private bool BrowserMenuOpen => _browserMenu is { IsOpen: true };

    private void ToggleBrowserMenu()
    {
        if (BrowserMenuOpen)
        {
            CloseBrowserMenu(restoreFocus: true);
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
        var frame = BuildBrowserMenu(popup, buttons);
        popup.Child = frame;
        // As teclas premidas no menu ficam na árvore do Popup (sem pai, não sobem até à janela — nem pelo MenuButton):
        // a raiz do menu entrega-as às mesmas funções da janela, e é Window_PreviewKeyDown que chama HandleBrowserMenuKey.
        frame.PreviewKeyDown += Window_PreviewKeyDown;
        frame.PreviewKeyUp += (_, e) => AltKeyUp(e.Key == Key.System ? e.SystemKey : e.Key);
        popup.Opened += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            if (!popup.IsOpen) return;
            // Com o foco na página, o foco do Win32 está na janela do Chromium (filha, noutra thread) e o Focus() do WPF
            // não o tira de lá: as teclas iam para a página. Pede-se o foco do Win32 para a janela do WPF.
            if (_browserMenuReturn is { ChildWindow: not 0 }) SetFocus(new WindowInteropHelper(this).Handle);
            FocusBrowserMenuButton(buttons.FirstOrDefault(button => button.IsEnabled) ?? buttons[0]);
        }, DispatcherPriority.Input);
        popup.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_browserMenu, popup)) return;
            _browserMenu = null;
            _browserMenuButtons = [];
            _browserMenuOpenForPage = false;
            // Fechado por um clique fora ou por a janela perder a ativação: se o teclado ficou preso no menu que fechou,
            // volta a quem o tinha (sem roubar a ativação a outra janela).
            var back = _browserMenuReturn;
            _browserMenuReturn = null;
            if (back is not null && (frame.IsKeyboardFocusWithin || Keyboard.FocusedElement is null) && IsActive) RestoreFocusAfterBrowserMenu(back);
        };
        _browserMenuReturn = CaptureBrowserMenuReturn();
        _browserMenu = popup;
        _browserMenuButtons = buttons;
        _browserMenuOpenForPage = true;
        popup.IsOpen = true;
    }

    // Fecha o menu. restoreFocus (Esc, Alt+F): o teclado volta a quem o tinha quando o menu abriu, como no Chrome.
    private void CloseBrowserMenu(bool restoreFocus = false)
    {
        _browserMenuOpenForPage = false;
        if (_browserMenu is not { IsOpen: true } open) return;
        var back = _browserMenuReturn;
        _browserMenuReturn = null;
        open.IsOpen = false;
        if (restoreFocus && back is not null) RestoreFocusAfterBrowserMenu(back);
    }

    private BrowserMenuReturn CaptureBrowserMenuReturn()
    {
        var window = new WindowInteropHelper(this).Handle;
        var focus = GetFocus();
        // Foco numa janela filha (a página, um painel web, o motor de vídeo): o WPF não sabe dela, guarda-se a janela.
        var child = focus != 0 && focus != window && IsChild(window, focus) ? focus : 0;
        return new BrowserMenuReturn(child != 0 ? null : Keyboard.FocusedElement, child);
    }

    private void RestoreFocusAfterBrowserMenu(BrowserMenuReturn back)
    {
        if (back.ChildWindow != 0)
        {
            if (ActiveBrowser is { } browser && browser.Handle != 0 && (browser.Handle == back.ChildWindow || IsChild(browser.Handle, back.ChildWindow)))
            {
                ReturnFocusToPage();
                // O Keyboard.Focus do WPF não basta se ele já achar que a página tem o foco: pede-se ao Win32.
                if (IsWindow(back.ChildWindow) && GetFocus() != back.ChildWindow) SetFocus(back.ChildWindow);
            }
            else if (IsWindow(back.ChildWindow)) SetFocus(back.ChildWindow);
            else ReturnFocusToPage();
            return;
        }
        if (back.Element is UIElement { IsVisible: true, IsEnabled: true } element && element.Focus()) return;
        MenuButton.Focus();
    }

    // (IsChild está em MainWindow.PageVisibility.)
    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial nint GetFocus();

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    private static CustomPopupPlacement[] BrowserMenuPlacement(Size popup, Size target, Point offset) =>
    [
        new(new Point(target.Width - popup.Width, target.Height + 6), PopupPrimaryAxis.Vertical),
        new(new Point(target.Width - popup.Width, -popup.Height - 6), PopupPrimaryAxis.Vertical),
    ];

    private Border BuildBrowserMenu(Popup popup, List<Button> buttons)
    {
        var theme = new ResourceDictionary { Source = new Uri($"pack://application:,,,/{typeof(MainWindow).Assembly.GetName().Name};component/Theme/BrowserMenu.xaml", UriKind.Absolute) };
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
        Row("\uE72E", "Privacidade: relatório e auto-teste", () => OpenInternal(PrivacyPage.Url));

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
        var heading = BrowserMenuHeading(IsPrivate);
        DockPanel.SetDock(heading, Dock.Top);
        content.Children.Add(heading);
        content.Children.Add(body);
        KeyboardNavigation.SetTabNavigation(content, KeyboardNavigationMode.Cycle);

        var frame = BrowserMenuFrame(content, Math.Clamp(ActualHeight - 74, 254, 665));
        frame.Resources.MergedDictionaries.Add(theme);
        return frame;
    }

    // Moldura do menu. O Popup não tem pai e herda a fonte do MenuButton (IconButton: fonte de ícones), por isso a
    // raiz fixa a fonte da interface; sem isto o título, o subtítulo e «Zoom» saíam em quadrados.
    internal static Border BrowserMenuFrame(UIElement content, double maxHeight)
    {
        var frame = CodePopup.WithUiFont(new Border
        {
            Child = content,
            Width = 332,
            MaxHeight = maxHeight,
            CornerRadius = new CornerRadius(13),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            SnapsToDevicePixels = true,
            Margin = new Thickness(0, 0, 1, 1),
        });
        frame.SetResourceReference(Border.BackgroundProperty, "SurfaceRaisedBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "FieldBorderBrush");
        AutomationProperties.SetName(frame, "Menu do Éteris");
        return frame;
    }

    internal static Border BrowserMenuHeading(bool isPrivate)
    {
        var title = new TextBlock { Text = "Éteris", FontSize = 17, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var label = new TextBlock
        {
            Text = isPrivate ? "JANELA PRIVADA" : "MENU DO NAVEGADOR",
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

    internal static Grid BrowserMenuLabel(string glyph, string title, string? shortcut = null)
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
        if (ReferenceEquals(_browserMenu, popup)) _browserMenuReturn = null;
        _browserMenuOpenForPage = false;
        popup.IsOpen = false;
        ReturnFocusToPage();
        action();
    }

    // Tecla premida com o menu aberto (vem de Window_PreviewKeyDown, pela raiz do menu). O Esc fecha o menu — não para
    // o carregamento — e um atalho fecha-o antes de abrir o que quer que abra (paleta, radar, transferências, aba…).
    // Enter, Espaço e Tab ficam com o WPF (o botão ativa-se, Tab percorre em ciclo).
    private bool HandleBrowserMenuKey(Key key, bool ctrl, bool shift, bool alt)
    {
        if (_browserMenu is not { IsOpen: true } popup) return false;
        switch (BrowserMenuKeys.Resolve(key, ctrl, shift, alt))
        {
            case BrowserMenuKey.Close:
                CloseBrowserMenu(restoreFocus: true);
                return true;
            case (BrowserMenuKey.Previous or BrowserMenuKey.Next or BrowserMenuKey.First or BrowserMenuKey.Last) and var move:
                MoveBrowserMenuFocus(move);
                return true;
        }
        if (Shortcut(key, ctrl, shift, alt) is { } action)
        {
            RunBrowserMenuAction(popup, action);
            return true;
        }
        return false;
    }

    // Tecla que chegou à página com o menu aberto (o foco do Win32 ainda lá estava), já decidida na thread do CEF por
    // BrowserMenuKeys.FromPage: aqui só se faz, na thread da janela.
    private void HandleBrowserMenuKeyFromPage(BrowserMenuKey decision, Action? shortcut)
    {
        if (_browserMenu is not { IsOpen: true } popup)
        {
            // O menu fechou entretanto: o atalho corre na mesma (a tecla já não chegou à página).
            shortcut?.Invoke();
            return;
        }
        switch (decision)
        {
            case BrowserMenuKey.Close:
                CloseBrowserMenu(restoreFocus: true);
                break;
            case BrowserMenuKey.Previous or BrowserMenuKey.Next or BrowserMenuKey.First or BrowserMenuKey.Last:
                MoveBrowserMenuFocus(decision);
                break;
            case BrowserMenuKey.Activate:
                if (_browserMenuButtons.FirstOrDefault(button => button.IsKeyboardFocused) is { IsEnabled: true } focused)
                    focused.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, focused));
                break;
            case BrowserMenuKey.NextField or BrowserMenuKey.PreviousField:
                if (_browserMenuButtons.Any(button => button.IsKeyboardFocused))
                    (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(
                        decision == BrowserMenuKey.NextField ? FocusNavigationDirection.Next : FocusNavigationDirection.Previous));
                else MoveBrowserMenuFocus(BrowserMenuKey.First);
                break;
            case BrowserMenuKey.Shortcut when shortcut is not null:
                RunBrowserMenuAction(popup, shortcut);
                break;
        }
    }

    private void MoveBrowserMenuFocus(BrowserMenuKey move)
    {
        var enabled = _browserMenuButtons.Where(button => button.IsEnabled).ToArray();
        var next = BrowserMenuKeys.Move(move, Array.FindIndex(enabled, button => button.IsKeyboardFocusWithin), enabled.Length);
        if (next >= 0) FocusBrowserMenuButton(enabled[next]);
    }

    private static void FocusBrowserMenuButton(Button button)
    {
        button.Focus();
        button.BringIntoView();
    }
}
