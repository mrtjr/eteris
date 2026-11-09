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
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Palette;
using Eteris.Browser.Sync;
using Eteris.Sync.Google;

namespace Eteris.Browser;

public partial class MainWindow
{
    private Popup? _browserMenu;
    private List<Button> _browserMenuButtons = [];

    private bool BrowserMenuOpen => _browserMenu is { IsOpen: true };

    // O botão do menu: Enter/Espaço nele abrem-no pelo teclado, um clique pelo rato. (Alt+F chama ToggleBrowserMenu(true)
    // diretamente: vindo da página, a tecla não passa pelo InputManager do WPF.)
    private void ToggleBrowserMenu() => ToggleBrowserMenu(byKeyboard: InputManager.Current.MostRecentInputDevice is KeyboardDevice);

    private void ToggleBrowserMenu(bool byKeyboard)
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
        popup.Child = BuildBrowserMenu(popup, buttons, out var hasAccountRow);
        // As teclas premidas no menu não ficam nele: o Popup não tem pai, por isso encaminha-as pelo MenuButton até à
        // janela, e é Window_PreviewKeyDown que as entrega a HandleBrowserMenuKey.
        popup.Opened += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            if (popup.IsOpen) FocusBrowserMenuButton(buttons[BrowserMenuFirstFocus(byKeyboard, hasAccountRow)]);
        }, DispatcherPriority.Input);
        popup.Closed += (_, _) =>
        {
            if (popup.Tag is Action detach) detach();
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

    // Que linha recebe o foco ao abrir. Pelo teclado, a primeira (a da conta, se houver: está primeiro no ecrã). Pelo
    // rato, a primeira ação (Nova aba), como antes da linha da conta: o convite «Entrar com o Google» não fica realçado
    // em cada abertura (no Chrome e no Firefox a conta só se realça ao passar o rato ou com o teclado).
    internal static int BrowserMenuFirstFocus(bool byKeyboard, bool hasAccountRow) => !byKeyboard && hasAccountRow ? 1 : 0;

    // Altura máxima do menu: a da janela, menos a barra de cima (como no Chrome e no Firefox, o menu cresce o que
    // precisar). Antes havia também um teto fixo de 665 px, a altura do menu sem a linha da conta: com ela,
    // «Atalhos de teclado» e «Fechar janela» ficavam escondidos atrás de uma barra de deslocamento.
    internal static double BrowserMenuMaxHeight(double windowHeight) => Math.Max(254, windowHeight - 74);

    private Border BuildBrowserMenu(Popup popup, List<Button> buttons, out bool hasAccountRow)
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
        body.Resources.Add(typeof(ScrollBar), theme["BrowserMenuScrollBar"]);
        var content = new DockPanel();
        var heading = BrowserMenuHeading(IsPrivate);
        DockPanel.SetDock(heading, Dock.Top);
        content.Children.Add(heading);
        hasAccountRow = false;
        if (BuildBrowserMenuAccount(popup, rowStyle) is { } account)
        {
            hasAccountRow = true;
            // Primeira na ordem do teclado, como está primeiro no ecrã.
            buttons.Insert(0, account);
            var holder = new StackPanel { Margin = new Thickness(6, 0, 6, 0) };
            holder.Children.Add(account);
            holder.Children.Add(BrowserMenuDivider());
            DockPanel.SetDock(holder, Dock.Top);
            content.Children.Add(holder);
        }
        content.Children.Add(body);
        KeyboardNavigation.SetTabNavigation(content, KeyboardNavigationMode.Cycle);

        var frame = BrowserMenuFrame(content, BrowserMenuMaxHeight(ActualHeight));
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

    // A linha da conta no topo do menu (como no Chrome, no Firefox e no Brave), só com a sincronização experimental
    // ligada, numa janela normal e com o identificador da aplicação (google-oauth.json): nas janelas privadas não
    // aparece nada da conta, e sem identificador «Entrar com o Google» não se pode cumprir (o botão das Definições fica
    // «Por configurar»), por isso o menu não o oferece.
    internal enum MenuAccountRow { None, SignIn, Account }

    internal static MenuAccountRow BrowserMenuAccountRow(bool syncPreview, bool isPrivate, bool configured, bool signedIn)
        => !syncPreview || isPrivate || !configured ? MenuAccountRow.None : signedIn ? MenuAccountRow.Account : MenuAccountRow.SignIn;

    private Button? BuildBrowserMenuAccount(Popup popup, Style rowStyle)
    {
        var syncPreview = Settings.Current.SyncPreview;
        // Só lê o disco (tokens cifrados) com a opção ligada; nada sai daqui para a Google.
        var configured = syncPreview && !IsPrivate && GoogleSync.Config is not null;
        var kind = BrowserMenuAccountRow(syncPreview, IsPrivate, configured, configured && GoogleSync.SignedIn);
        if (kind == MenuAccountRow.None) return null;
        var button = BrowserMenuAccountButton(kind, kind == MenuAccountRow.Account ? GoogleSync.Account : null, rowStyle);
        button.Click += (_, _) => RunBrowserMenuAction(popup, () => OpenInternal(SettingsPage.Url + "#sincronizacao"));
        if (kind == MenuAccountRow.Account && GoogleSync.Account is null)
        {
            // Já havia sessão sem conta guardada: abrir o menu (ação do utilizador) pede o nome e o e-mail, em segundo
            // plano, e a linha atualiza-se se o menu ainda estiver aberto.
            Action changed = () => Dispatcher.BeginInvoke(() =>
            {
                if (popup.IsOpen) FillBrowserMenuAccount(button, MenuAccountRow.Account, GoogleSync.Account);
            });
            GoogleSync.AccountChanged += changed;
            popup.Tag = (Action)(() => GoogleSync.AccountChanged -= changed);
            GoogleSync.RequestAccount();
        }
        return button;
    }

    internal static Button BrowserMenuAccountButton(MenuAccountRow kind, GoogleAccount? account, Style rowStyle)
    {
        var button = new Button { Style = rowStyle, Padding = new Thickness(8, 6, 8, 6) };
        FillBrowserMenuAccount(button, kind, account);
        return button;
    }

    // O que a linha diz. Com sessão: avatar com a inicial (círculo com o gradiente da íris; sem foto, que seria outro
    // pedido a outro servidor), nome em negrito, e-mail e o estado. Sem sessão: «Entrar com o Google».
    internal static void FillBrowserMenuAccount(Button button, MenuAccountRow kind, GoogleAccount? account)
    {
        var signedIn = kind == MenuAccountRow.Account;
        var known = signedIn && account is { IsEmpty: false };
        // Sem sessão, a linha abre a secção Sincronização das Definições (onde o início de sessão mostra o estado e, sem
        // outro navegador, o endereço para copiar): as reticências dizem que há mais um passo.
        var title = !signedIn ? "Entrar com o Google…"
            : known ? (string.IsNullOrWhiteSpace(account!.Name) ? account.Email! : account.Name!) : "Conta Google ligada";
        var detail = known && !string.IsNullOrWhiteSpace(account!.Name) && !string.IsNullOrWhiteSpace(account.Email) ? account.Email : null;
        var state = signedIn ? "Ligado ao Google Drive" : "Sincronização cifrada (experimental)";

        var avatar = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(16),
            Margin = new Thickness(0, 0, 11, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        var mark = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        if (known)
        {
            avatar.SetResourceReference(Border.BackgroundProperty, "IrisBrush");
            mark.Text = account!.Initial;
            mark.FontSize = 15;
            mark.FontWeight = FontWeights.SemiBold;
            // A tinta sobre a cor de destaque (grafite ou branco, conforme a cor e o tema), como o resto da interface.
            mark.SetResourceReference(TextBlock.ForegroundProperty, "AccentInkBrush");
        }
        else
        {
            avatar.BorderThickness = new Thickness(2);
            avatar.SetResourceReference(Border.BorderBrushProperty, "IrisBrush");
            mark.Text = "\uE77B";
            mark.FontSize = 14;
            mark.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            mark.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        }
        avatar.Child = mark;

        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        TextBlock Line(string text, double size, bool strong, bool muted)
        {
            var line = new TextBlock { Text = text, FontSize = size, TextTrimming = TextTrimming.CharacterEllipsis };
            if (strong) line.FontWeight = FontWeights.SemiBold;
            line.SetResourceReference(TextBlock.ForegroundProperty, muted ? "TextMutedBrush" : "TextBrush");
            lines.Children.Add(line);
            return line;
        }
        Line(title, 13, strong: true, muted: false);
        if (detail is not null) Line(detail, 12, strong: false, muted: true);
        Line(state, 11, strong: false, muted: true).Margin = new Thickness(0, 1, 0, 0);

        var row = new DockPanel();
        DockPanel.SetDock(avatar, Dock.Left);
        row.Children.Add(avatar);
        row.Children.Add(lines);
        button.Content = row;
        AutomationProperties.SetName(button, signedIn
            ? $"Conta Google: {(known ? account!.Label : "ligada")}. {state}. Abre a sincronização nas Definições"
            : "Entrar com o Google. Abre a sincronização nas Definições");
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
