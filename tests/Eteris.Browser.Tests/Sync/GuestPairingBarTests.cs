using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Eteris.Browser.Sync;

namespace Eteris.Browser.Tests.Sync;

// O cartão do computador novo e o alerta «conta substituída» (0.2.15, T7): o controlo numa thread STA e as regras da
// sua ligação à janela, pela fonte. O foco real do teclado prova-se ao vivo.
[Collection(WpfCollection.Name)]
public class GuestPairingBarTests
{
    private static readonly GuestPairingView Replaced = new(GuestPairingKind.Replaced);

    private static T Sta<T>(Func<T> work, string test) => StaRunner.Run(work, nameof(GuestPairingBarTests) + "." + test);

    private static string Src(params string[] parts) => Path.Combine([GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", .. parts]);

    private static IEnumerable<T> All<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var inner in All<T>(child)) yield return inner;
        }
    }

    private static void Press(Button button)
    {
        var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
        button.RaiseEvent(press);
        if (!press.Handled) throw new InvalidOperationException("o premir não foi tratado");
    }

    // Uma barra numa janela mostrada sem ativar (para as teclas terem uma origem), e fechada no fim.
    private static T InWindow<T>(GuestPairingBar bar, Func<T> work)
    {
        var window = new Window
        {
            Content = bar, Width = 600, Height = 300, Left = -32000, Top = -32000,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };
        window.Show();
        try { return work(); }
        finally { window.Close(); }
    }

    private static void Keys(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target) ?? throw new InvalidOperationException("sem origem");
        foreach (var routed in new[] { Keyboard.PreviewKeyDownEvent, Keyboard.KeyDownEvent })
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = routed });
    }

    private static string Shown(TextBlock block) => block.Visibility == Visibility.Visible ? block.Text : "";

    [Fact]
    public void GuestBar_NeverAsksForFocus_NoDefaultButton()
    {
        foreach (var file in new[] { Src("Sync", "GuestPairingBar.xaml.cs"), Src("MainWindow.GuestPairing.cs") })
        {
            var code = File.ReadAllText(file);
            Assert.DoesNotContain(".Focus(", code);
            Assert.DoesNotContain("Keyboard.Focus", code);
            Assert.DoesNotContain("FocusManager.SetFocusedElement", code);
            Assert.DoesNotContain("FocusInOverlay(", code);
        }
        var xaml = File.ReadAllText(Src("Sync", "GuestPairingBar.xaml"));
        Assert.DoesNotContain("IsDefault", xaml);
        Assert.DoesNotContain("IsCancel", xaml);
        Assert.DoesNotContain("Storyboard", xaml);
        Assert.DoesNotContain("RepeatBehavior", xaml);

        foreach (var view in Views)
        {
            var (barFocusable, focusable, defaults) = Sta(() =>
            {
                var bar = new GuestPairingBar();
                bar.Show(view);
                var buttons = All<Button>(bar).ToList();
                return (bar.Focusable, buttons.Where(b => b.Focusable).Select(b => b.Name).ToList(),
                    buttons.Where(b => b.IsDefault || b.IsCancel).Select(b => b.Name).ToList());
            }, nameof(GuestBar_NeverAsksForFocus_NoDefaultButton));
            Assert.False(barFocusable);
            Assert.Empty(focusable);
            Assert.Empty(defaults);
        }
    }

    // Os estados que a barra desenha (a ligação «Não tenho o outro computador à mão» também é um botão não focável).
    private static IEnumerable<GuestPairingView> Views => [Replaced];

    [Fact]
    public void GuestBar_EnterDoesNotConfirm()
    {
        foreach (var view in Views)
        {
            var fired = Sta(() =>
            {
                var bar = new GuestPairingBar();
                var count = 0;
                bar.ReplacedJoin += () => count++;
                bar.ReplacedNotMe += () => count++;
                bar.CloseRequested += () => count++;
                return InWindow(bar, () =>
                {
                    bar.Show(view);
                    // Os botões nunca têm o foco (teste acima): as teclas chegam à barra, e nenhuma confirma nada.
                    foreach (var key in new[] { Key.Enter, Key.Space })
                        Keys(bar, key);
                    return count;
                });
            }, nameof(GuestBar_EnterDoesNotConfirm));
            Assert.Equal(0, fired);
        }
    }

    [Fact]
    public void Replaced_ShowsTheAlert_WithTwoEqualButtons_AndNoClose()
    {
        var (title, body, consequence, buttons, close, code) = Sta(() =>
        {
            var bar = new GuestPairingBar();
            bar.Show(Replaced);
            var visible = All<Button>(bar).Where(b => b.Visibility == Visibility.Visible && b != bar.CloseButton).Select(b => (string)b.Content).ToList();
            return (Shown(bar.TitleText), Shown(bar.BodyText), Shown(bar.ConsequenceText), visible, bar.CloseButton.Visibility, Shown(bar.CodeText));
        }, nameof(Replaced_ShowsTheAlert_WithTwoEqualButtons_AndNoClose));
        Assert.Equal(GuestPairingModel.ReplacedTitle, title);
        Assert.Equal(GuestPairingModel.ReplacedText, body);
        Assert.Equal(GuestPairingModel.ReplacedConsequence, consequence);
        Assert.Equal([GuestPairingModel.ReplacedJoin, GuestPairingModel.ReplacedNotMe], buttons);
        Assert.Equal(Visibility.Collapsed, close);
        Assert.Equal("", code);
    }

    [Fact]
    public void Replaced_Buttons_RaiseTheirEvents_AndTheNoteStays()
    {
        var (join, notMe, note) = Sta(() =>
        {
            var bar = new GuestPairingBar();
            var (j, n) = (0, 0);
            bar.ReplacedJoin += () => j++;
            bar.ReplacedNotMe += () => n++;
            bar.Show(Replaced);
            Press(bar.ReplacedJoinButton);
            Press(bar.ReplacedNotMeButton);
            bar.ShowNote(SyncChoices.NotConfirmed("este computador não foi juntado à conta"));
            bar.Show(Replaced); // o RefreshBars seguinte não apaga a nota do mesmo estado
            return (j, n, Shown(bar.NoteText));
        }, nameof(Replaced_Buttons_RaiseTheirEvents_AndTheNoteStays));
        Assert.Equal(1, join);
        Assert.Equal(1, notMe);
        Assert.Equal("Não foi confirmado com o Windows: este computador não foi juntado à conta.", note);
    }
}
