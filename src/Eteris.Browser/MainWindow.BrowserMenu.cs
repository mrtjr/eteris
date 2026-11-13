using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using CefSharp;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Palette;
using Eteris.Browser.Sync;
using Eteris.Browser.Vault;
using Eteris.Sync.Google;

namespace Eteris.Browser;

public partial class MainWindow
{
    private Popup? _browserMenu;
    private List<Button> _browserMenuButtons = [];
    // Quem tinha o teclado quando o menu abriu (a página, a barra de endereço, o «⋯»…): recebe-o de volta ao fechar.
    private BrowserMenuReturn? _browserMenuReturn;
    // O menu fechou com a janela inativa (Alt+Tab, clique noutra aplicação) e o teclado estava nele: ao voltar à janela,
    // o teclado vai para quem o tinha (o Windows devolvia-o à janela do WPF, a um botão do menu que já fechou).
    private BrowserMenuReturn? _browserMenuReturnOnActivate;
    // Quem tinha o teclado antes de um clique no «⋯»: o clique dá o foco ao botão antes de o menu abrir.
    private BrowserMenuReturn? _browserMenuPressReturn;
    // Marca o painel dos controlos do zoom: para as setas, a linha do zoom é um grupo (↑ e ↓ contam-na uma vez).
    private static readonly object BrowserMenuZoomGroup = new();
    // Marca o painel do cartão da conta com o «Sair» à direita: para as setas, também uma linha só (→ vai ao «Sair»).
    private static readonly object BrowserMenuAccountGroup = new();
    // Lido na thread do CEF (ShortcutFromPage): o menu está aberto. Atualizado só na thread da janela, ao abrir e ao fechar.
    private volatile bool _browserMenuOpenForPage;
    // As partes do menu aberto que o submenu troca (a lista, a linha da conta, os botões do menu principal).
    private BrowserMenuParts? _browserMenuParts;

    private sealed class BrowserMenuParts
    {
        public required Popup Popup;
        public required Border Frame;
        public required ScrollViewer Body;
        public required StackPanel Main;
        public required List<Button> MainButtons;
        public required Style RowStyle;
        public FrameworkElement? Account;
        public BrowserMenuSubmenuButton? SubmenuRow;
        public bool InSubmenu;
    }

    private sealed record BrowserMenuReturn(IInputElement? Element, nint ChildWindow);

    private bool BrowserMenuOpen => _browserMenu is { IsOpen: true };

    // O botão do menu: Enter/Espaço nele abrem-no pelo teclado, um clique pelo rato. (Alt+F e Alt+E chamam
    // ToggleBrowserMenu(true) diretamente: vinda da página, a tecla não passa pelo InputManager do WPF.)
    private void ToggleBrowserMenu() => ToggleBrowserMenu(byKeyboard: InputManager.Current.MostRecentInputDevice is KeyboardDevice);

    private void ToggleBrowserMenu(bool byKeyboard)
    {
        var pressed = _browserMenuPressReturn;
        _browserMenuPressReturn = null;
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
        // A linha da conta (se houver) já está em buttons: conta como o primeiro item do menu.
        NumberBrowserMenuItems(buttons);
        popup.Child = frame;
        // As teclas premidas no menu ficam na árvore do Popup (sem pai, não sobem até à janela — nem pelo MenuButton):
        // a raiz do menu entrega-as às mesmas funções da janela, e é Window_PreviewKeyDown que chama HandleBrowserMenuKey.
        // A linha da conta está em buttons (a primeira): as setas, Home e End também passam por ela.
        frame.PreviewKeyDown += Window_PreviewKeyDown;
        frame.PreviewKeyUp += (_, e) => AltKeyUp(e.Key == Key.System ? e.SystemKey : e.Key);
        // Uma só seleção, como no Chrome, no Firefox e no Brave: passar o rato por uma linha leva-lhe o foco do teclado
        // (o Enter ativa e a ↓ continua a partir dela). Só conta o rato que se mexe de facto: a rolagem pelas setas
        // passa as linhas por baixo de um rato parado e o WPF gera um MouseMove sem que ele se mexa.
        Point? mouseAt = null;
        frame.PreviewMouseMove += (_, e) =>
        {
            var at = e.GetPosition(frame);
            if (mouseAt == at) return;
            mouseAt = at;
            // (_browserMenuButtons: os do submenu, quando está aberto.)
            if (_browserMenuButtons.FirstOrDefault(button => button.IsMouseOver && button.IsEnabled) is { IsKeyboardFocused: false } hovered) hovered.Focus();
        };
        popup.Opened += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            if (!popup.IsOpen) return;
            mouseAt = Mouse.GetPosition(frame);
            // Com o foco na página, o foco do Win32 está na janela do Chromium (filha, noutra thread) e o Focus() do WPF
            // não o tira de lá: as teclas iam para a página. Pede-se o foco do Win32 para a janela do WPF.
            if (_browserMenuReturn is { ChildWindow: not 0 }) SetFocus(new WindowInteropHelper(this).Handle);
            // Pelo teclado (Alt+F, Enter no «⋯») a primeira linha fica realçada (a da conta, se houver: está primeiro no
            // ecrã); pelo rato nenhuma, como no Chrome, no Firefox e no Brave: o teclado fica na raiz do menu e a primeira
            // ↓ (ou Tab) vai para a primeira linha. O convite «Entrar com o Google» não fica realçado a cada clique.
            if (BrowserMenuFirstFocus(byKeyboard) is var first and >= 0)
                FocusBrowserMenuButton(buttons[first].IsEnabled ? buttons[first] : buttons.FirstOrDefault(button => button.IsEnabled) ?? buttons[first]);
            else frame.Focus();
            if (AutomationPeer.ListenerExists(AutomationEvents.MenuOpened))
                UIElementAutomationPeer.CreatePeerForElement(frame)?.RaiseAutomationEvent(AutomationEvents.MenuOpened);
        }, DispatcherPriority.Input);
        popup.Closed += (_, _) =>
        {
            if (popup.Tag is Action detach) detach();
            if (!ReferenceEquals(_browserMenu, popup)) return;
            _browserMenu = null;
            _browserMenuButtons = [];
            _browserMenuParts = null;
            _browserMenuOpenForPage = false;
            _altTap.Reset();
            // Fechado sem Esc nem Alt+F (clique fora, mudança de aba, perda de ativação): se o teclado ficou preso no menu
            // que fechou, volta a quem o tinha. Com a janela (ou a camada) ativa, já; inativa, quando voltar a ser ativada
            // (sem roubar a ativação a outra janela).
            var back = _browserMenuReturn;
            _browserMenuReturn = null;
            if (back is null || !(frame.IsKeyboardFocusWithin || Keyboard.FocusedElement is null)) return;
            if (IsActive || _overlay is { IsActive: true }) RestoreFocusAfterBrowserMenu(back);
            else _browserMenuReturnOnActivate = back;
        };
        _browserMenuReturnOnActivate = null;
        // Aberto com um clique no «⋯»: o clique deu-lhe o foco, mas quem o tinha antes (a página…) é que o recebe de volta.
        _browserMenuReturn = !byKeyboard && pressed is not null && MenuButton.IsKeyboardFocused ? pressed : CaptureBrowserMenuReturn();
        _browserMenu = popup;
        _browserMenuButtons = buttons;
        _browserMenuOpenForPage = true;
        popup.IsOpen = true;
    }

    // Fecha o menu. restoreFocus (Esc, Alt+F): o teclado volta a quem o tinha quando o menu abriu, como no Chrome. Sem
    // restoreFocus (mudança de aba, perda de ativação, outra janela) quem decide é o Closed: o destino não se perde.
    private void CloseBrowserMenu(bool restoreFocus = false)
    {
        _browserMenuOpenForPage = false;
        if (_browserMenu is not { IsOpen: true } open) return;
        if (!restoreFocus)
        {
            open.IsOpen = false;
            return;
        }
        var back = _browserMenuReturn;
        _browserMenuReturn = null;
        open.IsOpen = false;
        if (back is not null) RestoreFocusAfterBrowserMenu(back);
    }

    // A janela voltou a ser ativada depois de o menu fechar com ela inativa: se o teclado não tem dono (o Windows pô-lo
    // na janela do WPF e o elemento focado era um botão do menu que fechou), volta a quem o tinha quando o menu abriu.
    // Um clique que reativa a janela numa página ou num campo ganha: aí o foco já está noutro sítio.
    private void RestoreFocusAfterBrowserMenuOnActivate()
    {
        if (_browserMenuReturnOnActivate is not { } back) return;
        _browserMenuReturnOnActivate = null;
        Dispatcher.InvokeAsync(() =>
        {
            if (!IsActive || BrowserMenuOpen || GetFocus() != new WindowInteropHelper(this).Handle) return;
            if (Keyboard.FocusedElement is DependencyObject focused && PresentationSource.FromDependencyObject(focused) is not null) return;
            RestoreFocusAfterBrowserMenu(back);
        }, DispatcherPriority.Input);
    }

    // O clique no «⋯» dá-lhe o foco antes de o menu abrir: guarda-se aqui quem o tinha (a página, a barra de endereço…).
    private void MenuButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _browserMenuPressReturn = BrowserMenuOpen ? null : CaptureBrowserMenuReturn();

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
            // A página de onde se abriu já não existe ou já não é a ativa (o menu fechou porque a aba mudou ou fechou): o
            // teclado vai para a página ativa, nunca para uma página escondida (nem para a outra metade do ecrã dividido).
            if (!IsWindow(back.ChildWindow) || IsInactivePageWindow(back.ChildWindow))
            {
                ReturnFocusToPage();
                return;
            }
            if (ActiveBrowser is { } browser && browser.Handle != 0 && (browser.Handle == back.ChildWindow || IsChild(browser.Handle, back.ChildWindow)))
            {
                ReturnFocusToPage();
                // O Keyboard.Focus do WPF não basta se ele já achar que a página tem o foco: pede-se ao Win32.
                if (IsWindow(back.ChildWindow) && GetFocus() != back.ChildWindow) SetFocus(back.ChildWindow);
            }
            else SetFocus(back.ChildWindow);
            return;
        }
        if (back.Element is UIElement { IsVisible: true, IsEnabled: true } element && element.Focus()) return;
        MenuButton.Focus();
    }

    private bool IsInactivePageWindow(nint window)
    {
        foreach (var (tab, page) in _browsers)
            if (!ReferenceEquals(tab, Tabs.Active) && page.Handle != 0 && (page.Handle == window || IsChild(page.Handle, window))) return true;
        return false;
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

    // Que linha recebe o foco ao abrir. Pelo teclado, a primeira (a da conta, se houver: está primeiro no ecrã). Pelo
    // rato, a primeira ação (Nova aba), como antes da linha da conta: o convite «Entrar com o Google» não fica realçado
    // em cada abertura (no Chrome e no Firefox a conta só se realça ao passar o rato ou com o teclado).
    internal static int BrowserMenuFirstFocus(bool byKeyboard) => byKeyboard ? 0 : -1;

    // Altura máxima do menu: a da janela, menos a barra de cima (como no Chrome e no Firefox, o menu cresce o que
    // precisar). Antes havia também um teto fixo de 665 px, a altura do menu sem a linha da conta: com ela,
    // «Atalhos de teclado» e «Fechar janela» ficavam escondidos atrás de uma barra de deslocamento.
    internal static double BrowserMenuMaxHeight(double windowHeight) => Math.Max(254, windowHeight - 74);

    private Border BuildBrowserMenu(Popup popup, List<Button> buttons)
    {
        var theme = new ResourceDictionary { Source = new Uri($"pack://application:,,,/{typeof(MainWindow).Assembly.GetName().Name};component/Theme/BrowserMenu.xaml", UriKind.Absolute) };
        var rowStyle = (Style)theme["BrowserMenuRow"];
        var controlStyle = (Style)theme["BrowserMenuControl"];
        var items = new StackPanel { Margin = new Thickness(6, 0, 6, 6) };

        void Row(string glyph, string label, Action action, string? shortcut = null, bool enabled = true)
        {
            var button = new BrowserMenuItemButton
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
        // Palavras-passe e preenchimento automático › (submenu com as cinco páginas, como no Chrome). Também na janela
        // privada: as entradas abrem numa janela normal.
        var submenuRow = SubmenuRow("\uE8D7", VaultPages.MenuTitle);
        Row("\uE896", "Transferências", ToggleDownloads, "Ctrl+J");
        Row("\uE71D", "Radar de abas", ToggleRadar, "Ctrl+Shift+A");
        Row("\uE708", "Pausa", TogglePause, "Ctrl+.");
        Row("\uE8A0", SidebarOpen ? "Fechar a barra lateral" : "Barra lateral", () => ToggleSidebar(), "Ctrl+Shift+B");

        Divider();
        items.Children.Add(BuildBrowserMenuZoom(popup, buttons, controlStyle));

        Divider();
        Row("\uE721", "Procurar na página…", OpenFind, "Ctrl+F", ActiveBrowser is not null);
        Row("\uE749", "Imprimir…", () => ReadyBrowser?.Print(), "Ctrl+P", ReadyBrowser is not null);
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
        var parts = new BrowserMenuParts { Popup = popup, Frame = null!, Body = body, Main = items, MainButtons = buttons, RowStyle = rowStyle, SubmenuRow = submenuRow };
        var heading = BrowserMenuHeading(IsPrivate);
        DockPanel.SetDock(heading, Dock.Top);
        content.Children.Add(heading);
        if (BuildBrowserMenuAccount(popup, rowStyle, out var accountKind) is { } account)
        {
            // Primeira na ordem do teclado, como está primeiro no ecrã.
            buttons.Insert(0, account);
            var holder = new StackPanel { Margin = new Thickness(6, 0, 6, 0) };
            // Com sessão, o «Sair» à direita do cartão (ao lado, não dentro: o clique nele não abre as Definições).
            if (BuildBrowserMenuSignOut(popup, accountKind, controlStyle) is { } signOut)
            {
                buttons.Insert(1, signOut);
                holder.Children.Add(BrowserMenuAccountCard(account, signOut));
            }
            else holder.Children.Add(account);
            holder.Children.Add(BrowserMenuDivider());
            DockPanel.SetDock(holder, Dock.Top);
            content.Children.Add(holder);
            parts.Account = holder;
        }
        content.Children.Add(body);
        KeyboardNavigation.SetTabNavigation(content, KeyboardNavigationMode.Cycle);

        var frame = BrowserMenuFrame(content, BrowserMenuMaxHeight(ActualHeight));
        frame.Resources.MergedDictionaries.Add(theme);
        parts.Frame = frame;
        _browserMenuParts = parts;
        return frame;

        BrowserMenuSubmenuButton SubmenuRow(string glyph, string label)
        {
            var button = new BrowserMenuSubmenuButton { Style = rowStyle, Content = BrowserMenuLabel(glyph, label, submenu: true) };
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) => OpenBrowserSubmenu(byKeyboard: InputManager.Current.MostRecentInputDevice is KeyboardDevice);
            buttons.Add(button);
            items.Children.Add(button);
            return button;
        }
    }

    // O submenu abre no lugar do menu, com «‹ Palavras-passe e preenchimento automático» no topo para voltar (como as
    // subvistas do menu do Firefox): as setas, as letras, o Enter e o leitor de ecrã continuam a ser os do menu.
    private void OpenBrowserSubmenu(bool byKeyboard)
    {
        if (_browserMenuParts is not { InSubmenu: false } parts || !parts.Popup.IsOpen) return;
        var panel = new StackPanel { Margin = new Thickness(6, 0, 6, 6) };
        var back = new Button { Style = parts.RowStyle, Content = BrowserMenuLabel("\uE72B", VaultPages.MenuTitle) };
        AutomationProperties.SetName(back, "Voltar ao menu do Éteris");
        back.Click += (_, _) => CloseBrowserSubmenu();
        panel.Children.Add(back);
        panel.Children.Add(BrowserMenuDivider());
        var buttons = new List<Button> { back };
        foreach (var entry in VaultPages.Entries)
        {
            var url = entry.Url;
            var button = new BrowserMenuItemButton { Style = parts.RowStyle, Content = BrowserMenuLabel(entry.Glyph, entry.Title) };
            AutomationProperties.SetName(button, entry.Title);
            button.Click += (_, _) => RunBrowserMenuAction(parts.Popup, () => OpenVaultPage(url));
            buttons.Add(button);
            panel.Children.Add(button);
        }
        NumberBrowserMenuItems(buttons);
        parts.InSubmenu = true;
        if (parts.SubmenuRow is { } row) row.IsExpanded = true;
        if (parts.Account is { } account) account.Visibility = Visibility.Collapsed;
        parts.Body.Content = panel;
        _browserMenuButtons = buttons;
        // Pelo teclado a primeira entrada fica realçada; pelo rato o teclado fica na raiz (a ↓ vai para a primeira).
        Dispatcher.InvokeAsync(() =>
        {
            if (!parts.Popup.IsOpen || !parts.InSubmenu) return;
            if (byKeyboard) FocusBrowserMenuButton(buttons[1]);
            else parts.Frame.Focus();
        }, DispatcherPriority.Input);
    }

    private void CloseBrowserSubmenu()
    {
        if (_browserMenuParts is not { InSubmenu: true } parts) return;
        parts.InSubmenu = false;
        parts.Body.Content = parts.Main;
        if (parts.Account is { } account) account.Visibility = Visibility.Visible;
        _browserMenuButtons = parts.MainButtons;
        if (parts.SubmenuRow is { } row)
        {
            row.IsExpanded = false;
            Dispatcher.InvokeAsync(() => { if (parts.Popup.IsOpen) FocusBrowserMenuButton(row); }, DispatcherPriority.Input);
        }
    }

    // O passo do submenu para uma tecla já decidida (Resolve na janela, FromPage na thread do CEF): true se o fez.
    private bool BrowserMenuSubmenuStep(BrowserMenuKey decision, Key key)
    {
        if (_browserMenuParts is not { } parts) return false;
        switch (BrowserMenuKeys.Submenu(decision, key, parts.InSubmenu, parts.SubmenuRow is { IsKeyboardFocused: true }))
        {
            case BrowserSubmenuStep.Open:
                OpenBrowserSubmenu(byKeyboard: true);
                return true;
            case BrowserSubmenuStep.Back:
                CloseBrowserSubmenu();
                return true;
            default:
                return false;
        }
    }

    // Moldura do menu. O Popup não tem pai e herda a fonte do MenuButton (IconButton: fonte de ícones), por isso a
    // raiz fixa a fonte da interface; sem isto o título, o subtítulo e «Zoom» saíam em quadrados.
    internal static Border BrowserMenuFrame(UIElement content, double maxHeight)
    {
        // A raiz recebe o teclado quando o menu abre pelo rato (nenhuma linha realçada) e é o «menu» para o leitor de ecrã.
        var frame = CodePopup.WithUiFont(new BrowserMenuSurface
        {
            Child = content,
            Focusable = true,
            FocusVisualStyle = null,
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

    // A linha da conta no topo do menu (como no Chrome, no Firefox e no Brave), numa janela normal e com o
    // identificador da aplicação (google-oauth.json): nas janelas privadas não aparece nada da conta, e sem
    // identificador «Entrar com o Google» não se pode cumprir (as Definições dizem «indisponível nesta compilação»),
    // por isso o menu não o oferece.
    internal enum MenuAccountRow { None, SignIn, Account }

    internal static MenuAccountRow BrowserMenuAccountRow(bool isPrivate, bool available, bool connected)
        => isPrivate || !available ? MenuAccountRow.None : connected ? MenuAccountRow.Account : MenuAccountRow.SignIn;

    private Button? BuildBrowserMenuAccount(Popup popup, Style rowStyle, out MenuAccountRow shown)
    {
        // Só lê o disco (tokens e estado cifrados); nada sai daqui para a Google.
        var available = !IsPrivate && SyncService.Available;
        var kind = BrowserMenuAccountRow(IsPrivate, available, available && SyncService.Connected);
        shown = kind;
        if (kind == MenuAccountRow.None) return null;
        var button = BrowserMenuAccountButton(kind, kind == MenuAccountRow.Account ? GoogleSync.Account : null, rowStyle);
        // A ação decide-se no clique (o estado pode mudar com o menu aberto); abrir o menu não inicia nada.
        button.Click += (_, _) => RunBrowserMenuAction(popup, () => RunBrowserMenuAccount(kind));
        if (kind != MenuAccountRow.Account) return button;
        // A linha atualiza-se se o menu ainda estiver aberto: a conta chegou, a sincronização acabou uma ronda, ou um
        // aparelho novo pediu para entrar.
        Action changed = () => Dispatcher.BeginInvoke(() =>
        {
            if (popup.IsOpen) FillBrowserMenuAccount(button, MenuAccountRow.Account, GoogleSync.Account);
        });
        GoogleSync.AccountChanged += changed;
        SyncService.Changed += changed;
        popup.Tag = (Action)(() =>
        {
            GoogleSync.AccountChanged -= changed;
            SyncService.Changed -= changed;
        });
        // Abrir o menu (ação do utilizador), com a sincronização ligada: pedidos de aparelhos novos e — se já havia
        // sessão sem conta guardada — o nome e o e-mail, em segundo plano. Com ela desligada, nada sai para a Google.
        SyncService.Look();
        if (SyncService.Enabled && GoogleSync.Account is null) GoogleSync.RequestAccount();
        return button;
    }

    // ---- «Sair» no cartão da conta (0.2.17) --------------------------------------------------------------------------

    // Só com sessão, fora da pasta de teste (lá é «Desligar», nas Definições); desativado enquanto corre outra saída da
    // conta («Retirar o acesso», «Perdi um computador», «Começar do zero»).
    internal enum MenuSignOut { Hidden, Enabled, Disabled }

    internal static MenuSignOut BrowserMenuSignOut(MenuAccountRow kind, bool folderMode, bool exitBusy)
        => kind != MenuAccountRow.Account || folderMode ? MenuSignOut.Hidden : exitBusy ? MenuSignOut.Disabled : MenuSignOut.Enabled;

    // Dois passos, como o «apagar também a chave» das Definições: o 1.º arma; um 2.º antes de meio segundo (duplo clique)
    // não conta; depois disso sai. Fechar o menu desarma (cada abertura faz um botão novo).
    internal enum MenuSignOutStep { Arm, Ignore, Run }

    internal const long BrowserMenuSignOutGuardMs = 500;

    // Uma tecla mantida premida (a repetição automática do Windows) nunca conta: nem arma, nem confirma.
    internal static MenuSignOutStep BrowserMenuSignOutStep(long? armedAtMs, long nowMs, bool repeat = false)
        => repeat ? MenuSignOutStep.Ignore
            : armedAtMs is not { } armed ? MenuSignOutStep.Arm
            : nowMs - armed < BrowserMenuSignOutGuardMs ? MenuSignOutStep.Ignore : MenuSignOutStep.Run;

    private Button? BuildBrowserMenuSignOut(Popup popup, MenuAccountRow kind, Style style)
    {
        var state = BrowserMenuSignOut(kind, SyncService.FolderMode, SyncService.ExitBusy is not null);
        if (state == MenuSignOut.Hidden) return null;
        var button = BrowserMenuSignOutButton(style);
        ApplyBrowserMenuSignOutState(button, state);
        long? armedAt = null;
        // O Enter num botão do WPF clica a cada KeyDown, também nas repetições: o clique a seguir sabe se foi uma.
        var repeat = false;
        button.PreviewKeyDown += (_, e) => repeat = e.IsRepeat;
        button.PreviewMouseLeftButtonDown += (_, _) => repeat = false;
        button.Click += (_, _) =>
        {
            var step = BrowserMenuSignOutStep(armedAt, Environment.TickCount64, repeat);
            repeat = false;
            switch (step)
            {
                case MenuSignOutStep.Arm:
                    armedAt = Environment.TickCount64;
                    ArmBrowserMenuSignOut(button);
                    break;
                case MenuSignOutStep.Run:
                    // Outra saída pode ter começado nas Definições com o menu aberto: aí não sai, diz porquê.
                    if (SyncService.ExitBusy is not null)
                    {
                        RunBrowserMenuAction(popup, () => ShowToast(SyncExit.SignOutBusy));
                        break;
                    }
                    RunBrowserMenuAction(popup, () =>
                    {
                        var hasKey = SyncService.HasKey;
                        try
                        {
                            SyncService.SignOutHere(forgetKey: false);
                            ShowToast(SyncExit.SignOutDoneText(hasKey));
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { ShowToast(SyncExit.SignOutNotSaved); }
                    });
                    break;
            }
        };
        return button;
    }

    // Ativo ou desativado; desativado (outra saída da conta a decorrer), a dica e a ajuda dizem porquê.
    internal static void ApplyBrowserMenuSignOutState(Button button, MenuSignOut state)
    {
        button.IsEnabled = state == MenuSignOut.Enabled;
        var why = state == MenuSignOut.Disabled ? SyncExit.SignOutBusy : null;
        button.ToolTip = why ?? "Sair deste computador";
        AutomationProperties.SetHelpText(button, why ?? SyncExit.SignOutHelp);
    }

    // «Sair» (o cartão é estreito); o nome acessível e a dica dizem o alcance, e a ajuda é a das Definições. Um botão,
    // como os do zoom: fora da contagem «n de N» das linhas.
    internal static Button BrowserMenuSignOutButton(Style style)
    {
        var button = new Button
        {
            Style = style, Content = "Sair", Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, ToolTip = "Sair deste computador",
        };
        ToolTipService.SetShowOnDisabled(button, true);
        AutomationProperties.SetName(button, "Sair deste computador");
        AutomationProperties.SetHelpText(button, SyncExit.SignOutHelp);
        return button;
    }

    // O 1.º toque: pede a confirmação no próprio botão (o menu fica aberto) e o leitor de ecrã ouve o nome novo.
    internal static void ArmBrowserMenuSignOut(Button button)
    {
        var old = AutomationProperties.GetName(button);
        const string Name = "Confirmar: sair deste computador";
        button.Content = "Confirmar";
        button.ToolTip = "Carregue outra vez para sair deste computador";
        AutomationProperties.SetName(button, Name);
        UIElementAutomationPeer.FromElement(button)?.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, old, Name);
    }

    // O cartão e o «Sair» lado a lado, no painel marcado como um grupo para as setas. O cartão é o 1.º filho: o Tab e o
    // leitor de ecrã seguem a ordem dos filhos, e assim seguem a do ecrã (cartão, depois «Sair»).
    internal static Grid BrowserMenuAccountCard(Button account, Button signOut)
    {
        var card = new Grid { Tag = BrowserMenuAccountGroup };
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.Children.Add(account);
        Grid.SetColumn(signOut, 1);
        card.Children.Add(signOut);
        return card;
    }

    // O que o clique na linha faz. Sem sessão entra logo (o navegador do sistema); com a sincronização em pausa por a
    // Google ter pedido para entrar outra vez, entra com a conta guardada como pista; com a sessão em ordem abre as
    // Definições. No modo da pasta de teste não há Google: «entrar» é ligar a pasta, como na rota das Definições.
    internal enum MenuAccountAction { OpenSettings, SignIn, SignInAgain, StartTestFolder }

    internal static MenuAccountAction BrowserMenuAccountAction(MenuAccountRow kind, bool folderMode, bool needsSignIn)
        => kind switch
        {
            MenuAccountRow.SignIn => folderMode ? MenuAccountAction.StartTestFolder : MenuAccountAction.SignIn,
            MenuAccountRow.Account when needsSignIn && !folderMode => MenuAccountAction.SignInAgain,
            _ => MenuAccountAction.OpenSettings,
        };

    private void RunBrowserMenuAccount(MenuAccountRow kind)
    {
        switch (BrowserMenuAccountAction(kind, SyncService.FolderMode, SyncService.Status?.NeedsSignIn == true))
        {
            case MenuAccountAction.SignIn: GoogleSync.BeginSignIn(); break;
            // O e-mail lê-se antes de BeginSignIn (que esquece a conta antiga) e vai só em memória.
            case MenuAccountAction.SignInAgain: GoogleSync.BeginSignIn(GoogleSync.ReentryHint()); break;
            case MenuAccountAction.StartTestFolder: SyncService.Start(); break;
            default: OpenInternal(SettingsPage.Url + "#sincronizacao"); break;
        }
    }

    internal static Button BrowserMenuAccountButton(MenuAccountRow kind, GoogleAccount? account, Style rowStyle)
    {
        // Item de menu como as outras linhas (para o leitor de ecrã e para a numeração «1 de N»).
        var button = new BrowserMenuItemButton { Style = rowStyle, Padding = new Thickness(8, 6, 8, 6) };
        FillBrowserMenuAccount(button, kind, account);
        return button;
    }

    // O que a linha diz. Com sessão: avatar com a inicial (círculo com o gradiente da íris; sem foto, que seria outro
    // pedido a outro servidor), nome em negrito, e-mail e o estado. Sem sessão: «Entrar com o Google».
    internal static void FillBrowserMenuAccount(Button button, MenuAccountRow kind, GoogleAccount? account)
    {
        var signedIn = kind == MenuAccountRow.Account;
        var known = signedIn && account is { IsEmpty: false };
        // Sem sessão, a linha abre o navegador do sistema para escolher a conta (BrowserMenuAccountAction): as
        // reticências dizem que há mais um passo, no navegador. Em pausa a ação é a mesma (a conta vai como pista).
        var folder = SyncService.FolderMode;
        var paused = signedIn && !folder && SyncService.Status?.NeedsSignIn == true;
        var title = !signedIn ? (folder ? "Ligar à pasta de teste…" : "Entrar com o Google…")
            : folder ? "Pasta de teste"
            : known ? (string.IsNullOrWhiteSpace(account!.Name) ? account.Email! : account.Name!) : "Conta Google ligada";
        var detail = !folder && known && !string.IsNullOrWhiteSpace(account!.Name) && !string.IsNullOrWhiteSpace(account.Email) ? account.Email : null;
        // Com sessão, o estado da sincronização («Sincronizado há 2 min», «A sincronizar…», um pedido de um aparelho
        // novo com o código); sem ela, o convite.
        var state = signedIn ? SyncService.Summary(SyncService.Status, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) : "Sincronize favoritos e palavras-passe, cifrados ponta a ponta";

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
        const string OpensBrowser = "Abre o seu navegador para escolher a conta Google";
        AutomationProperties.SetName(button, signedIn
            ? $"{(folder ? "Pasta de teste" : $"Conta Google: {(known ? account!.Label : "ligada")}")}. {state}. {(paused ? OpensBrowser : "Abre a sincronização nas Definições")}"
            : folder ? "Ligar à pasta de teste"
            : $"Entrar com o Google. {OpensBrowser}");
        // A dica só na linha que abre o navegador (sem sessão); em pausa, o nome acessível diz o mesmo.
        button.ToolTip = !signedIn && !folder ? OpensBrowser : null;
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
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Tag = BrowserMenuZoomGroup };
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

    internal static Grid BrowserMenuLabel(string glyph, string title, string? shortcut = null, bool submenu = false)
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
        if (submenu)
        {
            var chevron = new TextBlock { Text = "\uE76C", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            chevron.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            chevron.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            Grid.SetColumn(chevron, 2);
            grid.Children.Add(chevron);
        }
        return grid;
    }

    private static Border BrowserMenuDivider()
    {
        var divider = new Border { Height = 1, Margin = new Thickness(8, 4, 8, 4) };
        divider.SetResourceReference(Border.BackgroundProperty, "FieldBorderBrush");
        return divider;
    }


    // Posição de cada linha (item de menu) na lista, para o leitor de ecrã dizer «3 de 22» como no Chrome e no Brave.
    internal static void NumberBrowserMenuItems(IReadOnlyList<Button> buttons)
    {
        var items = buttons.OfType<BrowserMenuItemButton>().ToArray();
        for (var i = 0; i < items.Length; i++)
        {
            AutomationProperties.SetPositionInSet(items[i], i + 1);
            AutomationProperties.SetSizeOfSet(items[i], items.Length);
        }
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
    // Enter, Espaço e Tab numa linha ficam com o WPF (o botão ativa-se, Tab percorre em ciclo); com o teclado na raiz
    // (menu aberto pelo rato), Tab vai para a primeira linha e Shift+Tab para a última.
    private bool HandleBrowserMenuKey(Key key, bool ctrl, bool shift, bool alt)
    {
        if (_browserMenu is not { IsOpen: true } popup) return false;
        var decision = BrowserMenuKeys.Resolve(key, ctrl, shift, alt);
        if (BrowserMenuSubmenuStep(decision, key)) return true;
        switch (decision)
        {
            case BrowserMenuKey.Close:
                CloseBrowserMenu(restoreFocus: true);
                return true;
            case (BrowserMenuKey.Previous or BrowserMenuKey.Next or BrowserMenuKey.First or BrowserMenuKey.Last
                or BrowserMenuKey.PreviousInGroup or BrowserMenuKey.NextInGroup or BrowserMenuKey.Initial) and var move:
                MoveBrowserMenuFocus(move, key);
                return true;
            case (BrowserMenuKey.NextField or BrowserMenuKey.PreviousField) and var field when !_browserMenuButtons.Any(button => button.IsKeyboardFocused):
                MoveBrowserMenuFocus(field == BrowserMenuKey.NextField ? BrowserMenuKey.First : BrowserMenuKey.Last);
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
    private void HandleBrowserMenuKeyFromPage(BrowserMenuKey decision, Action? shortcut, Key key)
    {
        if (_browserMenu is not { IsOpen: true } popup)
        {
            // O menu fechou entretanto (clique fora, perda de ativação, mudança de aba): um atalho do Éteris corre na
            // mesma (a tecla já não chegou à página). As teclas do menu não fazem nada: o Alt+F que o queria fechar não
            // o reabre, e as setas, o Enter e o Tab já não têm onde atuar.
            if (decision == BrowserMenuKey.Shortcut) shortcut?.Invoke();
            return;
        }
        if (BrowserMenuSubmenuStep(decision, key)) return;
        switch (decision)
        {
            case BrowserMenuKey.Close:
                CloseBrowserMenu(restoreFocus: true);
                break;
            case BrowserMenuKey.Previous or BrowserMenuKey.Next or BrowserMenuKey.First or BrowserMenuKey.Last
                or BrowserMenuKey.PreviousInGroup or BrowserMenuKey.NextInGroup or BrowserMenuKey.Initial:
                MoveBrowserMenuFocus(decision, key);
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

    private void MoveBrowserMenuFocus(BrowserMenuKey move, Key key = Key.None)
    {
        var enabled = _browserMenuButtons.Where(button => button.IsEnabled).ToArray();
        var current = Array.FindIndex(enabled, button => button.IsKeyboardFocusWithin);
        var next = move == BrowserMenuKey.Initial
            ? BrowserMenuKeys.NextByInitial(key, current, BrowserMenuInitialLabels(enabled))
            : BrowserMenuKeys.Move(move, current, BrowserMenuRows(enabled));
        if (next >= 0) FocusBrowserMenuButton(enabled[next]);
    }

    // O nome de cada controlo para a inicial: as linhas contam (também o cartão da conta, num grupo com o «Sair»); os
    // controlos agrupados que não são linhas (−, 100%, + do zoom, o «Sair») não.
    internal static string?[] BrowserMenuInitialLabels(IReadOnlyList<Button> buttons)
        => buttons.Select(button => button is BrowserMenuItemButton || BrowserMenuGroupOf(button) == button ? AutomationProperties.GetName(button) : null).ToArray();

    // A linha de cada controlo, para as setas: os controlos do zoom partilham uma (são seguidos em buttons).
    internal static int[] BrowserMenuRows(IReadOnlyList<Button> buttons)
    {
        var rows = new int[buttons.Count];
        object? last = null;
        var row = -1;
        for (var i = 0; i < buttons.Count; i++)
        {
            var group = BrowserMenuGroupOf(buttons[i]);
            if (!ReferenceEquals(group, last)) row++;
            rows[i] = row;
            last = group;
        }
        return rows;
    }

    private static object BrowserMenuGroupOf(Button button)
        => button.Parent is FrameworkElement { Tag: var tag } panel && (ReferenceEquals(tag, BrowserMenuZoomGroup) || ReferenceEquals(tag, BrowserMenuAccountGroup)) ? panel : button;

    private static void FocusBrowserMenuButton(Button button)
    {
        button.Focus();
        button.BringIntoView();
    }
}

// Linha do menu para o leitor de ecrã: um item de menu (não «botão»), com a posição na lista (NumberBrowserMenuItems).
internal class BrowserMenuItemButton : Button
{
    protected override AutomationPeer OnCreateAutomationPeer() => new ItemPeer(this);

    private sealed class ItemPeer(Button owner) : ButtonAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.MenuItem;
        protected override string GetClassNameCore() => "BrowserMenuItem";
    }
}

// A linha com submenu («Palavras-passe e preenchimento automático ›»): um item de menu que o leitor de ecrã anuncia
// como fechado ou aberto (ExpandCollapse), como no Chrome.
internal sealed class BrowserMenuSubmenuButton : BrowserMenuItemButton
{
    private bool _expanded;

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            if (UIElementAutomationPeer.FromElement(this) is SubmenuPeer peer)
                peer.RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
                    value ? ExpandCollapseState.Collapsed : ExpandCollapseState.Expanded, value ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SubmenuPeer(this);

    private sealed class SubmenuPeer(BrowserMenuSubmenuButton owner) : ButtonAutomationPeer(owner), System.Windows.Automation.Provider.IExpandCollapseProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.MenuItem;
        protected override string GetClassNameCore() => "BrowserMenuItem";

        public override object GetPattern(PatternInterface patternInterface)
            => patternInterface == PatternInterface.ExpandCollapse ? this : base.GetPattern(patternInterface);

        public ExpandCollapseState ExpandCollapseState => owner.IsExpanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;

        public void Expand()
        {
            if (!owner.IsExpanded) owner.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, owner));
        }

        public void Collapse() { }
    }
}

// Raiz do menu: um «menu» com o nome «Menu do Éteris» para o leitor de ecrã. Um Border simples não tem AutomationPeer
// e o nome nunca chegava à UIA: ao abrir ouvia-se só «Nova aba, botão».
internal sealed class BrowserMenuSurface : Border
{
    protected override AutomationPeer OnCreateAutomationPeer() => new SurfacePeer(this);

    private sealed class SurfacePeer(BrowserMenuSurface owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Menu;
        protected override string GetClassNameCore() => "BrowserMenu";
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
    }
}
