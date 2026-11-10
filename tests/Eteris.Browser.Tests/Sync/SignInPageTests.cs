using System.Text.RegularExpressions;
using Eteris.Sync.Google;

namespace Eteris.Browser.Tests.Sync;

// A página que o recetor de loopback mostra no navegador do sistema no fim da entrada: só texto fixo, sem dados do
// utilizador, sem scripts e sem pedidos para fora.
public class SignInPageTests
{
    [Theory]
    [InlineData(SignInPageKind.FirstDevice, "Pronto.", "O Éteris começou a sincronizar. Pode fechar esta aba e voltar ao Éteris.")]
    [InlineData(SignInPageKind.NeedsApproval, "Falta um passo no Éteris.", "Volte ao Éteris: vai confirmar este computador noutro onde já entrou.")]
    [InlineData(SignInPageKind.Resumed, "Pronto.", "O Éteris retomou a sincronização. Pode fechar esta aba.")]
    [InlineData(SignInPageKind.DriveDenied, null, "A Google não deu ao Éteris acesso à pasta do Drive. Sem isso não há sincronização. Volte ao Éteris e tente outra vez.")]
    [InlineData(SignInPageKind.Neutral, null, "Sessão iniciada. Volte ao Éteris para continuar.")]
    [InlineData(SignInPageKind.Refused, null, "O Éteris não recebeu autorização. Volte ao Éteris e tente outra vez.")]
    public void Page_EveryCase_HasTheExactText(SignInPageKind kind, string? lead, string body)
    {
        Assert.Equal(lead, SignInPage.Lead(kind));
        Assert.Equal(body, SignInPage.Body(kind));
        var html = SignInPage.Html(kind);
        Assert.Contains("<p>" + body + "</p>", html, StringComparison.Ordinal);
        if (lead is not null) Assert.Contains("<h1>" + lead + "</h1>", html, StringComparison.Ordinal);
        else Assert.DoesNotContain("<h1>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Page_IsSelfContained_NoScriptNoExternalAddressNoEmail()
    {
        foreach (var kind in Enum.GetValues<SignInPageKind>())
        {
            var html = SignInPage.Html(kind);
            foreach (var banned in new[] { "http://", "https://", "<script", "javascript:", "<link", "<img", "<iframe", "<form", "src=", "href=", "url(", "@import", "window.close", "xmlns" })
                Assert.DoesNotContain(banned, html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(new Regex(@"//[a-z0-9-]+\.", RegexOptions.IgnoreCase), html);
            Assert.DoesNotMatch(new Regex(@"[^\s@<>]+@[^\s@<>]+\.[a-z]{2,}", RegexOptions.IgnoreCase), html);
        }
    }

    [Fact]
    public void Page_FollowsTheSystemTheme_AndCarriesTheInlineLogo()
    {
        foreach (var kind in Enum.GetValues<SignInPageKind>())
        {
            var html = SignInPage.Html(kind);
            Assert.Contains("prefers-color-scheme", html, StringComparison.Ordinal);
            Assert.Contains("<svg", html, StringComparison.Ordinal);
            Assert.Contains("charset=\"utf-8\"", html, StringComparison.Ordinal);
            Assert.Contains("<title>Éteris</title>", html, StringComparison.Ordinal);
            Assert.Contains("lang=\"pt-PT\"", html, StringComparison.Ordinal);
            Assert.Contains("name=\"viewport\"", html, StringComparison.Ordinal);
        }
    }
}
