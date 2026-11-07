using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

public class CreditsPageTests
{
    [Fact]
    public void ToHtml_RendersHeadingsTablesAndLinks()
    {
        var html = CreditsPage.ToHtml("""
            # Título
            Texto com `código` e https://exemplo.pt/x.

            | A | B |
            |---|---|
            | um | https://a.pt |
            """);
        Assert.Contains("<h1>Título</h1>", html);
        Assert.Contains("<code>código</code>", html);
        Assert.Contains("<th>A</th>", html);
        Assert.Contains("<td>um</td>", html);
        Assert.Contains("<a href=\"https://a.pt\">", html);
        Assert.DoesNotContain("---", html);
    }

    [Fact]
    public void ToHtml_EscapesMarkup()
        => Assert.DoesNotContain("<script>", CreditsPage.ToHtml("<script>alert(1)</script>"));

    [Fact]
    public void Render_IncludesTheBundledNotices()
    {
        var html = CreditsPage.Render();
        Assert.Contains("adblock-rust", html);
        Assert.Contains("GPL-3.0", html);
        Assert.Contains("CefSharp", html);
        // Os fundos da nova aba (imagens geradas por IA) também constam dos créditos.
        Assert.Contains("coast.jpg", html);
        Assert.Contains("coast-live.webm", html);
        Assert.Contains("geradas por", html);
    }
}
