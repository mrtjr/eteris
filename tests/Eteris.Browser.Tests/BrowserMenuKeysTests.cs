using System.IO;
using System.Windows;
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
    [InlineData(Key.Up, false, false, false, BrowserMenuKey.Previous)]
    [InlineData(Key.Left, false, false, false, BrowserMenuKey.Previous)]
    [InlineData(Key.Down, false, false, false, BrowserMenuKey.Next)]
    [InlineData(Key.Right, false, false, false, BrowserMenuKey.Next)]
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
    [InlineData(Key.A, false, false, false, BrowserMenuKey.None)]
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
    [InlineData(Key.A, false, false, false, false, BrowserMenuKey.Ignore)]
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
        Assert.Contains("FocusInOverlay(list);", File.ReadAllText(Path.Combine(src, "MainWindow.Shortcuts.cs")));
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
