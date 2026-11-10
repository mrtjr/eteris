using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Eteris.Browser.Sync;

namespace Eteris.Browser.Tests.Sync;

// A barra de aprovação (0.2.15, T8): o controlo numa thread STA e as regras da sua ligação à janela, pela fonte. Nada
// aqui depende do foco real do teclado (instável numa sessão de testes); esse prova-se ao vivo.
[Collection(WpfCollection.Name)]
public class PairingBarTests
{
    private const long Now = 1_762_560_000_000;

    private static readonly PairingBarView Code = new(PairingBarKind.Code, "sessao-1", "Portátil da sala", 3,
        PairingBarModel.Title(PairingBarModel.DefaultReceives));

    private static T Sta<T>(Func<T> work, string test) => StaRunner.Run(work, nameof(PairingBarTests) + "." + test);

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
    private static T InWindow<T>(Func<PairingBar, T> work)
    {
        var bar = new PairingBar();
        var window = new Window
        {
            Content = bar, Width = 600, Height = 200, Left = -32000, Top = -32000,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };
        window.Show();
        try { return work(bar); }
        finally { window.Close(); }
    }

    private static void Keys(UIElement target, Key key)
    {
        var source = PresentationSource.FromVisual(target)!;
        foreach (var routed in new[] { Keyboard.PreviewKeyDownEvent, Keyboard.KeyDownEvent })
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = routed });
    }

    [Fact]
    public void Showing_NeverAsksForFocus()
    {
        foreach (var file in new[] { Src("Sync", "PairingBar.xaml.cs"), Src("MainWindow.PairingBar.cs") })
        {
            var code = File.ReadAllText(file);
            Assert.DoesNotContain(".Focus(", code);
            Assert.DoesNotContain("Keyboard.Focus", code);
            Assert.DoesNotContain("FocusManager.SetFocusedElement", code);
        }
        var glue = File.ReadAllText(Src("MainWindow.PairingBar.cs"));
        Assert.Contains("ReturnFocusToPage()", glue);
        // A única chamada de foco permitida: a resposta ao clique na caixa.
        Assert.True(Regex.Matches(glue, @"FocusInOverlay\(").Count <= 1);

        var (barFocusable, buttonsFocusable, boxFocusable, boxTab) = Sta(() =>
        {
            var bar = new PairingBar();
            bar.Show(Code);
            return (bar.Focusable, All<Button>(bar).Any(b => b.Focusable), bar.CodeBox.Focusable, bar.CodeBox.IsTabStop);
        }, nameof(Showing_NeverAsksForFocus));
        Assert.False(barFocusable);
        Assert.False(buttonsFocusable);
        Assert.True(boxFocusable);
        Assert.True(boxTab);
    }

    [Fact]
    public void EnterAndSpace_OutsideTheBox_DoNothing()
    {
        var fired = Sta(() => InWindow(bar =>
        {
            var count = 0;
            bar.CodeEntered += (_, _) => count++;
            bar.NotMe += _ => count++;
            bar.LookRequested += () => count++;
            bar.RevokeRequested += () => count++;
            bar.AdviceClosed += () => count++;
            bar.Show(Code);
            bar.CodeBox.Text = "482 913";
            Keys(bar, Key.Enter);
            Keys(bar, Key.Space);
            bar.Show(new PairingBarView(PairingBarKind.Waiting, null, null, 0));
            Keys(bar, Key.Enter);
            Keys(bar, Key.Space);
            bar.ShowAdvice(PairingBarModel.RefusedAdvice);
            Keys(bar, Key.Enter);
            Keys(bar, Key.Space);
            return count;
        }), nameof(EnterAndSpace_OutsideTheBox_DoNothing));
        Assert.Equal(0, fired);
    }

    [Fact]
    public void Enter_InsideTheBox_SendsWhatWasTyped()
    {
        var (sent, left) = Sta(() => InWindow(bar =>
        {
            var sent = new List<(string, string)>();
            bar.CodeEntered += (session, typed) => sent.Add((session, typed));
            bar.Show(Code);
            bar.CodeBox.Text = "482 913";
            Keys(bar.CodeBox, Key.Enter);
            return (sent, bar.CodeBox.Text);
        }), nameof(Enter_InsideTheBox_SendsWhatWasTyped));
        Assert.Equal([("sessao-1", "482 913")], sent);
        // A caixa limpa-se depois de cada tentativa.
        Assert.Equal("", left);
    }

    [Fact]
    public void NoDefaultButton()
    {
        var buttons = Sta(() =>
        {
            var bar = new PairingBar();
            bar.Show(Code);
            return All<Button>(bar).Select(b => (b.IsDefault, b.IsCancel)).ToList();
        }, nameof(NoDefaultButton));
        Assert.NotEmpty(buttons);
        Assert.All(buttons, b => Assert.Equal((false, false), b));
        Assert.DoesNotContain("IsDefault", File.ReadAllText(Src("Sync", "PairingBar.xaml")));
        Assert.DoesNotContain("IsCancel", File.ReadAllText(Src("Sync", "PairingBar.xaml")));
    }

    [Fact]
    public void TheBar_HasNoCodeToCopy()
    {
        var texts = Sta(() =>
        {
            var bar = new PairingBar();
            bar.Show(Code);
            bar.ShowNote(PairingBarModel.WrongCode(2));
            return All<TextBlock>(bar).Select(t => t.Text).ToList();
        }, nameof(TheBar_HasNoCodeToCopy));
        Assert.NotEmpty(texts);
        Assert.DoesNotContain(texts, t => Regex.IsMatch(t, @"\d{3}\s?\d{3}"));
    }

    [Fact]
    public void NotMe_ShowsRevokeAdvice()
    {
        var (shown, revokeVisible, closeVisible, box, revoked, closed, notMe) = Sta(() =>
        {
            var bar = new PairingBar();
            var revoked = 0;
            var closed = 0;
            var notMe = new List<string>();
            bar.RevokeRequested += () => revoked++;
            bar.AdviceClosed += () => closed++;
            bar.NotMe += notMe.Add;
            bar.Show(Code);
            Press(bar.NotMeButton);
            bar.ShowAdvice(PairingBarModel.RefusedAdvice);
            var shown = bar.TitleText.Text;
            var revokeVisible = bar.RevokeButton.Visibility == Visibility.Visible && (string)bar.RevokeButton.Content == "Retirar o acesso do Éteris";
            var closeVisible = bar.CloseButton.Visibility == Visibility.Visible && (string)bar.CloseButton.Content == "Fechar";
            var box = bar.CodeFrame.Visibility;
            Press(bar.RevokeButton);
            Press(bar.CloseButton);
            return (shown, revokeVisible, closeVisible, box, revoked, closed, notMe);
        }, nameof(NotMe_ShowsRevokeAdvice));
        Assert.Equal(PairingBarModel.RefusedAdvice, shown);
        Assert.True(revokeVisible);
        Assert.True(closeVisible);
        Assert.NotEqual(Visibility.Visible, box);
        Assert.Equal(1, revoked);
        Assert.Equal(1, closed);
        Assert.Equal(["sessao-1"], notMe);

        // A ligação à janela: «Não fui eu» recusa o pedido e põe o conselho; «Retirar o acesso» vai para um só sítio.
        var glue = File.ReadAllText(Src("MainWindow.PairingBar.cs"));
        Assert.Contains("SyncService.RevokeAnchor", glue);
        Assert.DoesNotContain("\"#sincronizacao\"", glue);
        Assert.DoesNotContain("\"#syncRevokeItem\"", glue);
        Assert.Contains("RefusePairing(", glue);
        Assert.Contains("PairingBarModel.RefusedAdvice", glue);
        var service = File.ReadAllText(Src("Sync", "SyncService.cs"));
        Assert.Single(Regex.Matches(service, @"const string RevokeAnchor\b"));
        Assert.Equal("#sincronizacao", SyncService.RevokeAnchor);
    }

    [Fact]
    public void Texts_ToTheLetter_OnTheBar()
    {
        var (title, ask, waiting, connecting) = Sta(() =>
        {
            var bar = new PairingBar();
            bar.Show(Code);
            var title = bar.TitleText.Text;
            var ask = bar.AskText.Visibility == Visibility.Visible ? bar.AskText.Text : null;
            bar.Show(new PairingBarView(PairingBarKind.Waiting, null, null, 0));
            var waiting = (bar.TitleText.Text, bar.LookButton.Visibility == Visibility.Visible ? bar.LookButton.Content : null);
            bar.Show(new PairingBarView(PairingBarKind.Connecting, null, null, 0));
            return (title, ask, waiting, bar.TitleText.Text);
        }, nameof(Texts_ToTheLetter_OnTheBar));
        Assert.Equal(PairingBarModel.Title(PairingBarModel.DefaultReceives) + ".", title);
        Assert.Equal(PairingBarModel.Ask, ask);
        Assert.Equal((PairingBarModel.Waiting, (object?)"Ver"), waiting);
        Assert.Equal(PairingBarModel.Connecting, connecting);
    }

    [Fact]
    public void Name_StaysOutOfTheTitle_AndIsSmall()
    {
        var (title, name, smaller, weight) = Sta(() =>
        {
            var bar = new PairingBar();
            bar.Show(Code);
            return (bar.TitleText.Text, bar.NameText.Text, bar.NameText.FontSize < bar.TitleText.FontSize, bar.NameText.FontWeight);
        }, nameof(Name_StaysOutOfTheTitle_AndIsSmall));
        Assert.DoesNotContain("Portátil da sala", title);
        Assert.Equal(PairingBarModel.NameLine("Portátil da sala"), name);
        Assert.True(smaller);
        Assert.Equal(FontWeights.Normal, weight);
        // O título nunca recebe o nome.
        Assert.Single(typeof(PairingBarModel).GetMethod(nameof(PairingBarModel.Title))!.GetParameters());
    }

    [Fact]
    public void Box_TakesOnlyDigitsAndSpaces_AndClearsWhenTheSessionChanges()
    {
        var (maxLength, cleared, kept, noteCleared) = Sta(() =>
        {
            var bar = new PairingBar();
            bar.Show(Code);
            bar.CodeBox.Text = "482 9";
            bar.ShowNote(PairingBarModel.WrongCode(2));
            // A mesma sessão (outra ronda): nada muda.
            bar.Show(Code with { AttemptsLeft = 2 });
            var kept = bar.CodeBox.Text;
            bar.Show(Code with { Session = "sessao-2" });
            return (bar.CodeBox.MaxLength, bar.CodeBox.Text, kept, bar.NoteText.Visibility != Visibility.Visible);
        }, nameof(Box_TakesOnlyDigitsAndSpaces_AndClearsWhenTheSessionChanges));
        Assert.Equal(7, maxLength);
        Assert.Equal("482 9", kept);
        Assert.Equal("", cleared);
        Assert.True(noteCleared);
        Assert.True(PairingBar.Accepts("4 2"));
        Assert.False(PairingBar.Accepts("a"));
        Assert.False(PairingBar.Accepts("٤"));
    }
}
