using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Eteris.Browser.Sync;

namespace Eteris.Browser.Tests.Sync;

// O controlo da barra do início de sessão (0.2.15, T4) e as regras da sua ligação à janela. O controlo cria-se numa
// thread STA, sem janela; a ligação à janela confere-se pela fonte.
[Collection(WpfCollection.Name)]
public class SignInBarTests
{
    private static readonly SignInBarView NeedsApproval = new(SignInBarKind.NeedsApproval,
        "Sessão iniciada como Ana Teste. Falta confirmar no outro computador.", "Ana Teste",
        [new("Ver código", SignInBarAction.ShowCode, Primary: true), new("Fechar", SignInBarAction.Close)], null);

    private static readonly SignInBarView NoBrowser = new(SignInBarKind.NoBrowser,
        "Abra este endereço noutro navegador para entrar na Google.", null,
        [new("Copiar endereço", SignInBarAction.CopyAddress, Primary: true), new("Cancelar", SignInBarAction.Cancel)], null);

    private static T Sta<T>(Func<T> work, string test) => StaRunner.Run(work, nameof(SignInBarTests) + "." + test);

    private static List<Button> Buttons(SignInBar bar) => bar.Actions.Children.OfType<Button>().ToList();

    [Fact]
    public void Bar_Control_ShowsTheTextAndOneButtonPerAction()
    {
        var (text, bold, names, controlName, sameButtons, newButtons, kinds) = Sta(() =>
        {
            var bar = new SignInBar();
            bar.Show(NeedsApproval);
            var first = Buttons(bar);
            var text = new TextRange(bar.Message.ContentStart, bar.Message.ContentEnd).Text;
            var bold = bar.Message.Inlines.OfType<Bold>().SelectMany(b => b.Inlines.OfType<Run>()).Select(r => r.Text).SingleOrDefault();
            var names = first.Select(b => AutomationProperties.GetName(b)).ToArray();
            var controlName = AutomationProperties.GetName(bar);
            // A mesma vista outra vez (RefreshBars corre a cada evento): os botões não se refazem.
            bar.Show(NeedsApproval with { Buttons = [.. NeedsApproval.Buttons] });
            var same = Buttons(bar).SequenceEqual(first);
            bar.Show(NoBrowser);
            var kinds = Buttons(bar).Select(b => AutomationProperties.GetName(b)).ToArray();
            var renewed = !Buttons(bar).Intersect(first).Any();
            return (text, bold, names, controlName, same, renewed, kinds);
        }, nameof(Bar_Control_ShowsTheTextAndOneButtonPerAction));
        Assert.Equal(NeedsApproval.Text, text);
        Assert.Equal("Ana Teste", bold);
        Assert.Equal(["Ver código", "Fechar"], names);
        Assert.Equal(NeedsApproval.Text, controlName);
        Assert.True(sameButtons, "a mesma vista refez os botões");
        Assert.True(newButtons);
        Assert.Equal(["Copiar endereço", "Cancelar"], kinds);
    }

    [Fact]
    public void Bar_Control_RaisesActed_OnPress()
    {
        var acted = Sta(() =>
        {
            var bar = new SignInBar();
            var seen = new List<SignInBarAction>();
            bar.Acted += seen.Add;
            foreach (var view in new[] { NeedsApproval, NoBrowser })
            {
                bar.Show(view);
                foreach (var button in Buttons(bar))
                {
                    var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
                    button.RaiseEvent(press);
                    if (!press.Handled) throw new InvalidOperationException("o premir não foi tratado");
                }
            }
            return seen;
        }, nameof(Bar_Control_RaisesActed_OnPress));
        Assert.Equal([SignInBarAction.ShowCode, SignInBarAction.Close, SignInBarAction.CopyAddress, SignInBarAction.Cancel], acted);
    }

    // A ligação à janela: só em janelas normais, na camada, sem rasto dos eventos, com o teclado devolvido à página,
    // pelo ponto único das barras, e o endereço pela área de transferência segura. O arranque continua sem
    // sincronização (guarda de SyncIntegrationTests).
    [Fact]
    public void Bar_Source_Rules()
    {
        var src = Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser");
        var glue = File.ReadAllText(Path.Combine(src, "MainWindow.SignInBar.cs"));
        foreach (var needed in new[] { "IsPrivate", "AddToOverlay(", "GoogleSync.SignInChanged -=", "SyncService.Changed -=", "GoogleSync.AccountChanged -=",
                     "ReturnFocusToPage()", "RegisterBar(", "RefreshBars", "SecureClipboard.Shared.Copy(" })
            Assert.Contains(needed, glue);
        Assert.DoesNotContain("Clipboard.SetText", glue);
        var control = File.ReadAllText(Path.Combine(src, "Sync", "SignInBar.xaml.cs"));
        Assert.DoesNotContain("Clipboard.SetText", control);
        var xaml = File.ReadAllText(Path.Combine(src, "Sync", "SignInBar.xaml"));
        Assert.DoesNotContain("Storyboard", xaml);
        Assert.DoesNotContain("RepeatBehavior", xaml);
        Assert.DoesNotContain("Storyboard", control);
        Assert.DoesNotContain("RepeatBehavior", control);
        Assert.DoesNotContain("SignInBar", File.ReadAllText(Path.Combine(src, "MainWindow.xaml")));
        var window = File.ReadAllText(Path.Combine(src, "MainWindow.xaml.cs"));
        Assert.Contains("WireSignInBar()", window);
        Assert.DoesNotContain("SyncService", window);
        Assert.DoesNotContain("GoogleSync", window);
        // Só ShowInSignInBar (chamado pelos «show» das candidatas, logo pelo ponto único) mexe na visibilidade da barra.
        var showIn = Body(glue, "private void ShowInSignInBar(");
        Assert.Matches(@"_signInBar\.Visibility\s*=\s*Visibility\.Visible", showIn);
        Assert.Matches(@"_signInBar\.Visibility\s*=\s*Visibility\.Collapsed", showIn);
        Assert.Empty(System.Text.RegularExpressions.Regex.Matches(glue.Replace(showIn, ""), @"Visibility\s*=\s*Visibility\.Visible|\.Visibility\s*="));
        Assert.DoesNotContain("Visibility =", control);
        // Com tudo parado, a candidata responde sem criar nada nem ler a sincronização (R6).
        var wants = Body(glue, "private bool SignInBarWants()");
        var idle = wants.IndexOf("SignInStage.Idle", StringComparison.Ordinal);
        Assert.True(idle >= 0);
        Assert.True(idle < wants.IndexOf("SyncService.Agent()", StringComparison.Ordinal));
        Assert.True(idle < wants.IndexOf("GoogleSync.Account", StringComparison.Ordinal));
    }

    // O corpo de um método (da assinatura até à chaveta que o fecha, com a indentação de membro).
    private static string Body(string code, string signature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"não encontrei {signature}");
        var end = code.IndexOf("\n    }", start, StringComparison.Ordinal);
        return code[start..end];
    }
}
