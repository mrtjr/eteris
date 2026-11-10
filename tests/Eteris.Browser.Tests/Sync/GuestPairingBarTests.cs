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
    private static IEnumerable<GuestPairingView> Views =>
    [
        Replaced, new(GuestPairingKind.Waiting), new(GuestPairingKind.Code, "123 456"), new(GuestPairingKind.Code, "123 456", Confirmed: true),
        new(GuestPairingKind.Joined), new(GuestPairingKind.TimeUp), new(GuestPairingKind.StillWaiting),
        new(GuestPairingKind.Refused, Error: "O outro aparelho recusou o pedido."), new(GuestPairingKind.Failed, Error: "Sem ligação ao Google Drive."),
    ];

    // O que a barra mostra num estado: título, código, corpo, linha pequena, botões à vista (pela ordem), ligação, ×.
    private sealed record Drawn(string Title, string Code, string Body, string Line, string[] Buttons, bool Link, bool Close);

    private static Drawn Draw(GuestPairingBar bar, GuestPairingView view)
    {
        bar.Show(view);
        var buttons = new[] { bar.YesButton, bar.NoButton, bar.AskAgainButton, bar.ReplacedJoinButton, bar.ReplacedNotMeButton }
            .Where(b => b.Visibility == Visibility.Visible).Select(b => (string)b.Content).ToArray();
        return new(Shown(bar.TitleText), Shown(bar.CodeText), Shown(bar.BodyText), Shown(bar.LineText), buttons,
            bar.NoOtherLink.Visibility == Visibility.Visible, bar.CloseButton.Visibility == Visibility.Visible);
    }

    [Fact]
    public void Card_Texts_ToTheLetter_InEachState()
    {
        var drawn = Sta(() =>
        {
            var bar = new GuestPairingBar(() => 0);
            return Views.Skip(1).Select(view => Draw(bar, view)).ToList();
        }, nameof(Card_Texts_ToTheLetter_InEachState));
        Assert.Equal((GuestPairingModel.WaitTitle, "", GuestPairingModel.WaitText, GuestPairingModel.MustBeOpen, true, true),
            (drawn[0].Title, drawn[0].Code, drawn[0].Body, drawn[0].Line, drawn[0].Link, drawn[0].Close));
        Assert.Empty(drawn[0].Buttons);
        Assert.Equal((GuestPairingModel.TypeThisCode, "123 456", GuestPairingModel.Ask("123 456"), "", true, true),
            (drawn[1].Title, drawn[1].Code, drawn[1].Body, drawn[1].Line, drawn[1].Link, drawn[1].Close));
        Assert.Equal([GuestPairingModel.Yes, GuestPairingModel.No], drawn[1].Buttons);
        Assert.Equal((GuestPairingModel.TypeThisCode, "123 456", GuestPairingModel.WaitingApproval, false, true),
            (drawn[2].Title, drawn[2].Code, drawn[2].Body, drawn[2].Link, drawn[2].Close));
        Assert.Empty(drawn[2].Buttons);
        Assert.Equal(("", "", GuestPairingModel.Joined, false, true), (drawn[3].Title, drawn[3].Code, drawn[3].Body, drawn[3].Link, drawn[3].Close));
        Assert.Empty(drawn[3].Buttons);
        Assert.Equal((GuestPairingModel.TimeUp, "", "", "", true, true), (drawn[4].Title, drawn[4].Code, drawn[4].Body, drawn[4].Line, drawn[4].Link, drawn[4].Close));
        Assert.Equal([GuestPairingModel.AskAgain], drawn[4].Buttons);
        Assert.Equal((GuestPairingModel.StillWaiting, "", GuestPairingModel.MustBeOpen, true, true), (drawn[5].Title, drawn[5].Body + drawn[5].Code, drawn[5].Line, drawn[5].Link, drawn[5].Close));
        Assert.Equal([GuestPairingModel.AskAgain], drawn[5].Buttons);
        Assert.Equal(("", "O outro aparelho recusou o pedido.", true), (drawn[6].Title, drawn[6].Body, drawn[6].Link));
        Assert.Equal([GuestPairingModel.AskAgain], drawn[6].Buttons);
        Assert.Equal(("", "Sem ligação ao Google Drive.", true), (drawn[7].Title, drawn[7].Body, drawn[7].Link));
        Assert.Equal([GuestPairingModel.AskAgain], drawn[7].Buttons);
        // O código só aparece no estado Code.
        Assert.Equal(2, drawn.Count(d => d.Code.Length > 0));
        var labels = Sta(() =>
        {
            var bar = new GuestPairingBar();
            return ((string)bar.NoOtherLink.Content, bar.CloseButton.ToolTip as string, System.Windows.Automation.AutomationProperties.GetName(bar.CloseButton));
        }, nameof(Card_Texts_ToTheLetter_InEachState));
        Assert.Equal((GuestPairingModel.NoOther, GuestPairingModel.Close, GuestPairingModel.Close), labels);
    }

    [Fact]
    public void Card_Buttons_RaiseTheirEvents()
    {
        var events = Sta(() =>
        {
            var t = 0L;
            var bar = new GuestPairingBar(() => t);
            var seen = new List<string>();
            bar.Confirmed += () => seen.Add("sim");
            bar.Denied += () => seen.Add("não");
            bar.AskAgain += () => seen.Add("outra vez");
            bar.NoOtherComputer += () => seen.Add("sem o outro");
            bar.CloseRequested += () => seen.Add("fechar");
            bar.Show(new GuestPairingView(GuestPairingKind.Code, "123 456"));
            t += GuestPairingModel.ConfirmDelayMs;
            Press(bar.YesButton);
            Press(bar.NoButton);
            Press(bar.NoOtherLink);
            Press(bar.CloseButton);
            bar.Show(new GuestPairingView(GuestPairingKind.StillWaiting));
            Press(bar.AskAgainButton);
            return seen;
        }, nameof(Card_Buttons_RaiseTheirEvents));
        Assert.Equal(["sim", "não", "sem o outro", "fechar", "outra vez"], events);
    }

    [Fact]
    public void GuestBar_SimIsIgnoredForTwoSeconds()
    {
        var counts = Sta(() =>
        {
            var t = 1_000L;
            var bar = new GuestPairingBar(() => t);
            var confirmed = 0;
            bar.Confirmed += () => confirmed++;
            var seen = new List<int>();
            bar.Show(new GuestPairingView(GuestPairingKind.Code, "123 456"));
            Press(bar.YesButton);
            seen.Add(confirmed);
            t += 1_999;
            Press(bar.YesButton);
            seen.Add(confirmed);
            t += 1;
            Press(bar.YesButton);
            seen.Add(confirmed);
            // O mesmo código outra vez (cada RefreshBars) não reinicia a espera; outro código reinicia.
            bar.Show(new GuestPairingView(GuestPairingKind.Code, "123 456"));
            Press(bar.YesButton);
            seen.Add(confirmed);
            bar.Show(new GuestPairingView(GuestPairingKind.Code, "654 321"));
            Press(bar.YesButton);
            seen.Add(confirmed);
            t += 2_000;
            Press(bar.YesButton);
            seen.Add(confirmed);
            return seen;
        }, nameof(GuestBar_SimIsIgnoredForTwoSeconds));
        Assert.Equal([0, 0, 1, 2, 2, 3], counts);
    }

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
                bar.Confirmed += () => count++;
                bar.Denied += () => count++;
                bar.AskAgain += () => count++;
                bar.NoOtherComputer += () => count++;
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
