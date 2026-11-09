using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace Eteris.Browser.Tests;

// Defeito confirmado ao vivo: com o menu do Éteris aberto (Alt+F ou «⋯»), o Esc não o fechava, o Alt+F também não, e
// Home, End e a seta para cima não mexiam; aberto com o foco na página, as setas rolavam a página e o Esc chegava-lhe.
// Duas causas: (1) as teclas de um Popup sem pai não sobem até à janela (nem pelo PlacementTarget); (2) com o foco na
// página, o foco do Win32 fica no Chromium e as teclas vão para ShortcutFromPage, que não sabia do menu.
public class BrowserMenuKeysTests
{
    [Theory]
    [InlineData(Key.Escape, false, false, false, BrowserMenuKey.Close)]
    [InlineData(Key.Escape, false, true, false, BrowserMenuKey.Close)]
    [InlineData(Key.F, false, false, true, BrowserMenuKey.Close)]
    // Alt+E abre e fecha o menu, como no Chrome e no Brave.
    [InlineData(Key.E, false, false, true, BrowserMenuKey.Close)]
    [InlineData(Key.Up, false, false, false, BrowserMenuKey.Previous)]
    [InlineData(Key.Down, false, false, false, BrowserMenuKey.Next)]
    // ← e → só andam dentro da linha do zoom (já não descem nem sobem uma linha).
    [InlineData(Key.Left, false, false, false, BrowserMenuKey.PreviousInGroup)]
    [InlineData(Key.Right, false, false, false, BrowserMenuKey.NextInGroup)]
    [InlineData(Key.Home, false, false, false, BrowserMenuKey.First)]
    [InlineData(Key.End, false, false, false, BrowserMenuKey.Last)]
    [InlineData(Key.Enter, false, false, false, BrowserMenuKey.Activate)]
    [InlineData(Key.Space, false, false, false, BrowserMenuKey.Activate)]
    [InlineData(Key.Tab, false, false, false, BrowserMenuKey.NextField)]
    [InlineData(Key.Tab, false, true, false, BrowserMenuKey.PreviousField)]
    // Com modificadores não são do menu: Alt+← é «Voltar», Ctrl+Tab muda de aba, Shift+↓ não faz nada no menu.
    [InlineData(Key.Left, false, false, true, BrowserMenuKey.None)]
    [InlineData(Key.Tab, true, false, false, BrowserMenuKey.None)]
    [InlineData(Key.Down, false, true, false, BrowserMenuKey.None)]
    [InlineData(Key.F, true, false, false, BrowserMenuKey.None)]
    [InlineData(Key.F, true, false, true, BrowserMenuKey.None)]
    // AltGr+E (Ctrl+Alt) escreve «€»: não é do menu.
    [InlineData(Key.E, true, false, true, BrowserMenuKey.None)]
    // Uma letra salta para a linha com essa inicial; com Shift não é do menu.
    [InlineData(Key.A, false, false, false, BrowserMenuKey.Initial)]
    [InlineData(Key.H, false, true, false, BrowserMenuKey.None)]
    [InlineData(Key.D1, false, false, false, BrowserMenuKey.None)]
    public void Resolve_MenuKeys_LikeChromeFirefoxBrave(Key key, bool ctrl, bool shift, bool alt, BrowserMenuKey expected)
        => Assert.Equal(expected, BrowserMenuKeys.Resolve(key, ctrl, shift, alt));

    // A decisão de ShortcutFromPage (thread do CEF) com o menu aberto: as setas, o Esc, o Enter e o Espaço nunca chegam à
    // página (o Esc fecha o menu, não para o carregamento); os atalhos do Éteris continuam a correr (fechando o menu).
    [Theory]
    [InlineData(Key.Escape, false, false, false, false, BrowserMenuKey.Close)]
    [InlineData(Key.F, false, false, true, true, BrowserMenuKey.Close)]
    [InlineData(Key.Down, false, false, false, false, BrowserMenuKey.Next)]
    [InlineData(Key.Up, false, false, false, false, BrowserMenuKey.Previous)]
    [InlineData(Key.Home, false, false, false, false, BrowserMenuKey.First)]
    [InlineData(Key.End, false, false, false, false, BrowserMenuKey.Last)]
    [InlineData(Key.Enter, false, false, false, false, BrowserMenuKey.Activate)]
    [InlineData(Key.Space, false, false, false, false, BrowserMenuKey.Activate)]
    [InlineData(Key.Tab, false, true, false, false, BrowserMenuKey.PreviousField)]
    [InlineData(Key.T, true, false, false, true, BrowserMenuKey.Shortcut)]
    [InlineData(Key.N, true, true, false, true, BrowserMenuKey.Shortcut)]
    [InlineData(Key.F5, false, false, false, true, BrowserMenuKey.Shortcut)]
    [InlineData(Key.Left, false, false, true, true, BrowserMenuKey.Shortcut)]
    [InlineData(Key.PageDown, false, false, false, false, BrowserMenuKey.Ignore)]
    [InlineData(Key.Space, false, true, false, false, BrowserMenuKey.Ignore)]
    [InlineData(Key.A, false, false, false, false, BrowserMenuKey.Initial)]
    [InlineData(Key.E, false, false, true, false, BrowserMenuKey.Close)]
    [InlineData(Key.Right, false, false, false, false, BrowserMenuKey.NextInGroup)]
    [InlineData(Key.D1, false, false, false, false, BrowserMenuKey.Ignore)]
    [InlineData(Key.Down, false, true, false, false, BrowserMenuKey.Ignore)]
    [InlineData(Key.LeftAlt, false, false, true, false, BrowserMenuKey.PassToPage)]
    [InlineData(Key.LeftCtrl, true, false, false, false, BrowserMenuKey.PassToPage)]
    [InlineData(Key.RightShift, false, true, false, false, BrowserMenuKey.PassToPage)]
    public void FromPage_WithMenuOpen_ConsumesMenuKeysAndRunsShortcuts(Key key, bool ctrl, bool shift, bool alt, bool isShortcut, BrowserMenuKey expected)
        => Assert.Equal(expected, BrowserMenuKeys.FromPage(key, ctrl, shift, alt, isShortcut));

    [Fact]
    public void FromPage_ArrowsEscEnterSpace_NeverPassToPage()
    {
        foreach (var key in new[] { Key.Up, Key.Down, Key.Left, Key.Right, Key.Home, Key.End, Key.Escape, Key.Enter, Key.Space, Key.PageUp, Key.PageDown, Key.Tab })
            foreach (var shift in new[] { false, true })
                Assert.NotEqual(BrowserMenuKey.PassToPage, BrowserMenuKeys.FromPage(key, false, shift, false, isShortcut: false));
    }

    [Theory]
    [InlineData(BrowserMenuKey.Next, -1, 5, 0)]
    [InlineData(BrowserMenuKey.Next, 0, 5, 1)]
    [InlineData(BrowserMenuKey.Next, 4, 5, 0)]
    [InlineData(BrowserMenuKey.Previous, 0, 5, 4)]
    [InlineData(BrowserMenuKey.Previous, -1, 5, 4)]
    [InlineData(BrowserMenuKey.Previous, 3, 5, 2)]
    [InlineData(BrowserMenuKey.First, 3, 5, 0)]
    [InlineData(BrowserMenuKey.Last, 0, 5, 4)]
    [InlineData(BrowserMenuKey.Next, -1, 0, -1)]
    public void Move_WrapsAtBothEnds(BrowserMenuKey move, int current, int count, int expected)
        => Assert.Equal(expected, BrowserMenuKeys.Move(move, current, count));

    // O menu com a linha do zoom: 0 Barra lateral · 1-4 zoom (−, 100%, +, ecrã inteiro) · 5 Procurar · 6 Imprimir.
    // Como no Chrome e no Brave: ↑ e ↓ contam a linha do zoom uma vez (entram pelo primeiro controlo), ← e → só andam
    // dentro dela e numa linha simples não fazem nada.
    private static readonly int[] ZoomRows = [0, 1, 1, 1, 1, 2, 3];

    [Theory]
    [InlineData(BrowserMenuKey.Next, 0, 1)]
    [InlineData(BrowserMenuKey.Next, 1, 5)]
    [InlineData(BrowserMenuKey.Next, 3, 5)]
    [InlineData(BrowserMenuKey.Previous, 5, 1)]
    [InlineData(BrowserMenuKey.Previous, 4, 0)]
    [InlineData(BrowserMenuKey.Previous, 0, 6)]
    [InlineData(BrowserMenuKey.Next, 6, 0)]
    [InlineData(BrowserMenuKey.NextInGroup, 1, 2)]
    [InlineData(BrowserMenuKey.NextInGroup, 4, 4)]
    [InlineData(BrowserMenuKey.PreviousInGroup, 3, 2)]
    [InlineData(BrowserMenuKey.PreviousInGroup, 1, 1)]
    [InlineData(BrowserMenuKey.NextInGroup, 0, 0)]
    [InlineData(BrowserMenuKey.PreviousInGroup, 5, 5)]
    [InlineData(BrowserMenuKey.NextInGroup, -1, -1)]
    [InlineData(BrowserMenuKey.Next, -1, 0)]
    [InlineData(BrowserMenuKey.Previous, -1, 6)]
    [InlineData(BrowserMenuKey.Last, 2, 6)]
    [InlineData(BrowserMenuKey.First, 5, 0)]
    public void Move_ZoomRowIsOneGroup(BrowserMenuKey move, int current, int expected)
        => Assert.Equal(expected, BrowserMenuKeys.Move(move, current, ZoomRows));

    // A linha do zoom é a última: ↑ a partir da primeira e End entram nela pelo primeiro controlo.
    [Fact]
    public void Move_LastRowIsAGroup_EntersAtItsFirstControl()
    {
        int[] rows = [0, 1, 2, 2, 2];
        Assert.Equal(2, BrowserMenuKeys.Move(BrowserMenuKey.Previous, 0, rows));
        Assert.Equal(2, BrowserMenuKeys.Move(BrowserMenuKey.Last, 0, rows));
        Assert.Equal(0, BrowserMenuKeys.Move(BrowserMenuKey.Next, 4, rows));
    }

    // Navegação pela inicial: a próxima linha (com volta) cujo nome começa pela letra, sem acentos nem maiúsculas; os
    // controlos do zoom (null) não contam.
    [Theory]
    [InlineData(Key.N, -1, 0)]
    [InlineData(Key.N, 0, 1)]
    [InlineData(Key.N, 1, 0)]
    [InlineData(Key.H, -1, 2)]
    [InlineData(Key.E, -1, 4)]
    [InlineData(Key.T, 2, 3)]
    [InlineData(Key.T, 3, 3)]
    [InlineData(Key.Z, 0, -1)]
    [InlineData(Key.R, -1, -1)]
    [InlineData(Key.D1, -1, -1)]
    public void NextByInitial_JumpsToTheNextRowWithThatLetter(Key key, int current, int expected)
        => Assert.Equal(expected, BrowserMenuKeys.NextByInitial(key, current, ["Nova aba", "nova janela", "Histórico", "Transferências", "Éteris", null]));

    // Leitor de ecrã: a raiz do menu é um «menu» com nome e cada linha um «item de menu» com a posição na lista (antes
    // o nome estava num Border sem AutomationPeer e as linhas eram «botão»).
    [Fact]
    public void Automation_MenuWithNamedRoot_RowsAreMenuItemsWithPosition()
    {
        var (rootType, rootName, rowType, position, size, zoomType) = OnSta(() =>
        {
            var rows = new List<Button> { new BrowserMenuItemButton(), new BrowserMenuItemButton(), new Button(), new BrowserMenuItemButton() };
            MainWindow.NumberBrowserMenuItems(rows);
            var frame = MainWindow.BrowserMenuFrame(new StackPanel(), 400);
            var root = UIElementAutomationPeer.CreatePeerForElement(frame);
            var row = UIElementAutomationPeer.CreatePeerForElement(rows[1]);
            var zoom = UIElementAutomationPeer.CreatePeerForElement(rows[2]);
            return (root.GetAutomationControlType(), root.GetName(), row.GetAutomationControlType(),
                AutomationProperties.GetPositionInSet(rows[3]), AutomationProperties.GetSizeOfSet(rows[3]), zoom.GetAutomationControlType());
        });
        Assert.Equal(AutomationControlType.Menu, rootType);
        Assert.Equal("Menu do Éteris", rootName);
        Assert.Equal(AutomationControlType.MenuItem, rowType);
        Assert.Equal(3, position);
        Assert.Equal(3, size);
        Assert.Equal(AutomationControlType.Button, zoomType);
    }

    // As setas tratam a linha do zoom como um grupo: os seus controlos (no painel marcado) partilham uma linha.
    [Fact]
    public void BrowserMenuRows_ZoomControlsShareARow()
    {
        var rows = OnSta(() =>
        {
            var source = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "MainWindow.BrowserMenu.cs"));
            Assert.Contains("new StackPanel { Orientation = Orientation.Horizontal, Tag = BrowserMenuZoomGroup }", source);
            var marker = typeof(MainWindow).GetField("BrowserMenuZoomGroup", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null);
            var zoom = new StackPanel { Tag = marker };
            var buttons = new List<Button> { new(), new(), new(), new() };
            var top = new StackPanel();
            top.Children.Add(buttons[0]);
            zoom.Children.Add(buttons[1]);
            zoom.Children.Add(buttons[2]);
            top.Children.Add(buttons[3]);
            return MainWindow.BrowserMenuRows(buttons);
        });
        Assert.Equal([0, 1, 1, 2], rows);
    }

    // A causa (1), reproduzida no WPF: uma tecla num botão de um Popup sem pai não chega à janela do PlacementTarget —
    // só a quem estiver na árvore do Popup. Por isso a raiz do menu é que entrega as teclas a Window_PreviewKeyDown.
    [Fact]
    public void ParentlessPopup_KeysDoNotReachTheWindow_OnlyThePopupRoot()
    {
        var (window, root) = OnSta(() =>
        {
            var anchor = new Button { Width = 34, Height = 34 };
            var window = new Window
            {
                Content = anchor, Width = 120, Height = 80, Left = -32000, Top = -32000,
                WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            };
            window.Show();
            var row = new Button { Content = "Nova aba" };
            var frame = new Border { Child = row };
            var popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.AbsolutePoint, HorizontalOffset = -4000, VerticalOffset = -4000, Child = frame };
            popup.IsOpen = true;
            var windowSaw = 0;
            var rootSaw = 0;
            window.PreviewKeyDown += (_, _) => windowSaw++;
            anchor.PreviewKeyDown += (_, _) => windowSaw++;
            frame.PreviewKeyDown += (_, _) => rootSaw++;
            var source = PresentationSource.FromVisual(row)!;
            row.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            popup.IsOpen = false;
            window.Close();
            return (windowSaw, rootSaw);
        });
        Assert.Equal(0, window);
        Assert.Equal(1, root);
    }

    // Guardas no código: a raiz do menu liga as teclas à janela, ShortcutFromPage consulta o campo seguro entre threads
    // antes de deixar passar a tecla, e o menu fecha ao perder a ativação, ao mudar de aba e ao abrir outra janela.
    [Fact]
    public void Source_WiresMenuKeysAndClosesMenuOnWindowAndTabChanges()
    {
        var src = Path.Combine(Root(), "src", "Eteris.Browser");
        var menu = File.ReadAllText(Path.Combine(src, "MainWindow.BrowserMenu.cs"));
        var window = File.ReadAllText(Path.Combine(src, "MainWindow.xaml.cs"));
        Assert.Contains("frame.PreviewKeyDown += Window_PreviewKeyDown;", menu);
        Assert.Contains("private volatile bool _browserMenuOpenForPage;", menu);
        Assert.Contains("if (_browserMenuOpenForPage)", window);
        Assert.Contains("BrowserMenuKeys.FromPage(key, ctrl, shift, alt, shortcut is not null)", window);
        Assert.Contains("Deactivated += (_, _) => CloseBrowserMenu();", window);
        Assert.Contains("return;\n        CloseBrowserMenu();\n        OnActiveTabChanging();", window.Replace("\r\n", "\n"));
        Assert.Contains("other.CloseBrowserMenu();", File.ReadAllText(Path.Combine(src, "MainWindow.Private.cs")));
        // A folha de atalhos (F1) aberta a partir da página dá o teclado à camada: as setas já não rolam a página.
        var sheet = File.ReadAllText(Path.Combine(src, "MainWindow.Shortcuts.cs"));
        Assert.Contains("FocusInOverlay(list);", sheet);
        // …e a lista que recebe o foco tem nome para o leitor de ecrã.
        Assert.Contains("AutomationProperties.SetName(list, \"Atalhos de teclado\");", sheet);
    }

    // Revisão da correção: o foco não se perde quando o menu fecha sem Esc nem Alt+F, e as teclas vindas de páginas
    // seguem todas a mesma regra do menu.
    [Fact]
    public void Source_FocusReturnsAfterDismissAndPageKeysFollowTheMenu()
    {
        var src = Path.Combine(Root(), "src", "Eteris.Browser");
        var menu = File.ReadAllText(Path.Combine(src, "MainWindow.BrowserMenu.cs")).Replace("\r\n", "\n");
        var window = File.ReadAllText(Path.Combine(src, "MainWindow.xaml.cs"));
        // Fechado por mudança de aba ou perda de ativação, o destino não se deita fora: o Closed restaura já (janela ou
        // camada ativa) ou guarda-o para o próximo Activated.
        Assert.Contains("if (IsActive || _overlay is { IsActive: true }) RestoreFocusAfterBrowserMenu(back);\n            else _browserMenuReturnOnActivate = back;", menu);
        Assert.Contains("Activated += (_, _) => RestoreFocusAfterBrowserMenuOnActivate();", window);
        // Nunca para uma página escondida (a aba mudou) nem para uma janela que já não existe (a aba fechou).
        Assert.Contains("if (!IsWindow(back.ChildWindow) || IsInactivePageWindow(back.ChildWindow))", menu);
        // Com o menu já fechado, só um atalho do Éteris corre: o Alt+F que o queria fechar não o reabre.
        Assert.Contains("if (decision == BrowserMenuKey.Shortcut) shortcut?.Invoke();", menu);
        // As teclas próprias dos sites fixados também fecham o menu primeiro.
        Assert.Contains("if (BrowserMenuOpen) CloseBrowserMenu(restoreFocus: true);", File.ReadAllText(Path.Combine(src, "MainWindow.WebPanels.cs")));
        // Um clique no «⋯» não troca o destino: guarda-se quem tinha o teclado antes de o botão o receber.
        Assert.Contains("PreviewMouseLeftButtonDown=\"MenuButton_PreviewMouseLeftButtonDown\"", File.ReadAllText(Path.Combine(src, "MainWindow.xaml")));
        // Aberto pelo rato, nenhuma linha realçada (o teclado fica na raiz); o rato que se mexe leva o foco à linha.
        Assert.Contains("else frame.Focus();", menu);
        Assert.Contains("is { IsKeyboardFocused: false } hovered) hovered.Focus();", menu);
        Assert.Contains("if (alt && !ctrl && !shift && key is Key.F or Key.E) return () => ToggleBrowserMenu(byKeyboard: true);", window);
    }

    private static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception e) { error = e; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
        return result;
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
