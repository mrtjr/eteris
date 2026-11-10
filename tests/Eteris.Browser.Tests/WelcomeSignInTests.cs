using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Tests.Sync;

namespace Eteris.Browser.Tests;

// Boas-vindas (0.2.15, T13): o passo «Já usa o Éteris noutro computador?» antes de trazer as palavras-passe, só sem
// sessão; o clique vai por uma rota com o token e não sai da página; o rodapé deixa de dizer «Sem contas».
public class WelcomeSignInTests
{
    private const string Title = "Já usa o Éteris noutro computador?";
    private const string Text = "Entre com o Google para trazer favoritos, palavras-passe e definições. O conteúdo vai cifrado: a chave fica nos seus computadores, a Google não a tem.";

    [Fact]
    public void Welcome_HasTheSignInStep_BeforeImportingPasswords()
    {
        var html = WelcomePage.Render(new UserSettings(), [], offerSignIn: true);
        Assert.Contains(Title, html);
        Assert.Contains(Text, html);
        Assert.Matches(new Regex(@"<button[^>]*id=""signInGo""[^>]*>Entrar com o Google</button>"), html);
        Assert.Matches(new Regex(@"<button[^>]*id=""signInSkip""[^>]*>Agora não</button>"), html);
        Assert.Contains("\"signIn\":true", html);
        Assert.True(html.IndexOf(Title, StringComparison.Ordinal) < html.IndexOf("Trazer palavras-passe do Chrome, Brave ou Firefox", StringComparison.Ordinal));
    }

    [Fact]
    public void Welcome_WithoutTheOffer_KeepsTheStepHidden()
    {
        var html = WelcomePage.Render(new UserSettings(), [], offerSignIn: false);
        Assert.Contains("\"signIn\":false", html);
        Assert.Matches(new Regex(@"<section[^>]*id=""signInStep""[^>]*\bhidden\b"), html);
    }

    [Fact]
    public void Welcome_FooterNoLongerSaysNoAccounts()
    {
        var html = WelcomePage.Render(new UserSettings(), []);
        Assert.Contains("<b>Sem conta obrigatória.</b> Sem telemetria. Sem sincronização, tudo fica neste computador.", html);
        Assert.Contains("Sem conta obrigatória.", html);
        Assert.Contains("Sem sincronização, tudo fica neste computador.", html);
        Assert.DoesNotContain("Sem contas.", html);
    }

    [Fact]
    public void Welcome_SignInGoesThroughTheTokenRoute_AndTheCspIsUnchanged()
    {
        var html = WelcomePage.Render(new UserSettings(), [], offerSignIn: true);
        Assert.Contains("/sync-google?t=", html);
        Assert.DoesNotContain("location.href = 'eteris://settings", html);
        Assert.Contains("content=\"default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; frame-src eteris:\"", html);
        Assert.DoesNotContain("connect-src", html);
    }

    // A rota nova faz ela própria as duas recusas: a da janela privada das rotas sync-* só vale para o host settings.
    [Fact]
    public void WelcomeRoute_NeedsTheToken_AndRefusesPrivateWindows()
    {
        var factory = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Browser", "EterisSchemeHandlerFactory.cs")).Replace("\r\n", "\n");
        var start = factory.IndexOf("Host: \"welcome\", AbsolutePath: \"/sync-google\"", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var general = factory.IndexOf("Host: \"settings\" or \"about\" or \"welcome\"", StringComparison.Ordinal);
        Assert.True(start < general);
        var block = factory[start..factory.IndexOf("\n        }\n", start, StringComparison.Ordinal)];
        Assert.Contains("privateContext", block);
        Assert.Contains("InternalPages.IsAuthorized(request.Url)", block);
        Assert.Contains("Sync.SignInEntry.Begin()", block);
    }

    // Revisão T13: o clique não navega o iframe persistente «sink» (Atrás/Avançar repunha /sync-google nele e abria a
    // Google sem gesto; e um tema escolhido a seguir cancelava o pedido). Um iframe novo por clique, removido no load.
    [Fact]
    public void Welcome_SignInClick_UsesItsOwnFrame_NotTheSharedSink()
    {
        var html = WelcomePage.Render(new UserSettings(), [], offerSignIn: true).Replace("\r\n", "\n");
        Assert.DoesNotMatch(new Regex(@"sink\.src\s*=\s*`[^`]*sync-google"), html);
        var start = html.IndexOf("signInGo.addEventListener('click'", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var handler = html[start..html.IndexOf("\n  });\n", start, StringComparison.Ordinal)];
        Assert.Contains("/sync-google?t=", handler);
        Assert.Contains("document.createElement('iframe')", handler);
        Assert.Contains(".remove()", handler);
        Assert.DoesNotMatch(new Regex(@"\bsink\b|\bsend\("), handler);
    }

    // Revisão T13: com a sessão já ligada (outra janela, ou a página reposta pela história), a rota não começa outro
    // início de sessão. Só disco, como a regra que mostra o passo.
    [Fact]
    public void WelcomeRoute_RefusesWhenTheOfferNoLongerHolds()
    {
        var factory = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "Browser", "EterisSchemeHandlerFactory.cs")).Replace("\r\n", "\n");
        var start = factory.IndexOf("Host: \"welcome\", AbsolutePath: \"/sync-google\"", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var block = factory[start..factory.IndexOf("\n        }\n", start, StringComparison.Ordinal)];
        Assert.Contains("!Sync.SignInEntry.OffersNow(false)", block);
        Assert.True(block.IndexOf("SignInEntry.OffersNow(", StringComparison.Ordinal) < block.IndexOf("Sync.SignInEntry.Begin()", StringComparison.Ordinal));
    }
}
