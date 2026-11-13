using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

public class ReaderScriptsTests
{
    [Theory]
    [InlineData("https://www.publico.pt/artigo", true)]
    [InlineData("http://example.com/", true)]
    [InlineData("eteris://history/", false)]
    [InlineData("file:///C:/a.html", false)]
    public void OnlyWebPagesAreCandidates(string url, bool expected)
        => Assert.Equal(expected, ReaderScripts.IsCandidate(url));

    [Fact]
    public void Toggle_ReadsAloudOnlyWithLocalVoicesAndStopsOnClose()
    {
        Assert.Contains("speechSynthesis", ReaderScripts.Toggle);
        Assert.Contains("localService", ReaderScripts.Toggle);
        Assert.Contains("stopSpeech(); host.remove()", ReaderScripts.Toggle);
        Assert.Contains("speechSynthesis.cancel(); open.remove()", ReaderScripts.Toggle);
    }

    [Fact]
    public void Toggle_IsolatesStylesFromThePage()
    {
        Assert.Contains("attachShadow", ReaderScripts.Toggle);
        Assert.Contains("adoptedStyleSheets", ReaderScripts.Toggle);
        // O nosso código não usa innerHTML (sites com Trusted Types). O Readability usa-o em casos raros, protegidos pela reserva.
        var ours = ReaderScripts.Toggle[ReaderScripts.Toggle.IndexOf("const ID = 'eteris-reader'", StringComparison.Ordinal)..];
        Assert.DoesNotContain("innerHTML", ours);
    }

    [Fact]
    public void ToggleWith_StartsWithTheRememberedLook()
    {
        var script = ReaderScripts.ToggleWith("sepia", 23, false);
        Assert.Contains("let theme = 'sepia';", script);
        Assert.Contains("let size = 23;", script);
        Assert.Contains("(false ? ' serif' : ' sans')", script);
        foreach (var marker in new[] { "__THEME__", "__SIZE__", "__SERIF__" })
            Assert.DoesNotContain(marker, ReaderScripts.ToggleWith("<script>", 99, true));
        Assert.Contains("let theme = 'dark';", ReaderScripts.ToggleWith("<script>", 99, true));
        Assert.Contains("let size = 28;", ReaderScripts.ToggleWith("dark", 99, true));
    }

    [Theory]
    [InlineData("eteris-reader {\"theme\":\"light\",\"size\":18,\"serif\":false}", "light", 18, false)]
    [InlineData("eteris-reader {\"theme\":\"sepia\",\"size\":28,\"serif\":true}", "sepia", 28, true)]
    public void ParseReport_ReadsValidPreferences(string message, string theme, int size, bool serif)
        => Assert.Equal((theme, size, serif), ReaderScripts.ParseReport(message));

    [Theory]
    [InlineData("eteris-reader {\"theme\":\"light\",\"size\":18,\"serif\":false}")]
    [InlineData("eteris-reader {\"theme\":\"neon\",\"size\":18,\"serif\":false}")]
    [InlineData("eteris-reader {\"theme\":\"dark\",\"size\":90,\"serif\":false}")]
    [InlineData("eteris-reader não é json")]
    [InlineData(null)]
    public void ParseReport_IgnoresAnythingElse(string? message) => Assert.Null(ReaderScripts.ParseReport(message));

    [Fact]
    public void Toggle_UsesFirefoxReadabilityWithOurExtractorAsFallback()
    {
        var script = ReaderScripts.ToggleWith("dark", 20, true);
        Assert.Contains("function Readability(", script);
        Assert.Contains("serializer: el => el", script);
        Assert.Contains("if (!extracted) {", script);
        // O Readability fica dentro da função: não se torna uma variável global da página.
        Assert.True(script.IndexOf("function Readability(", StringComparison.Ordinal) > script.IndexOf("(() => {", StringComparison.Ordinal));
    }

    [Fact]
    public void Report_FromTabWithoutReader_IsIgnored()
    {
        Assert.False(ReaderScripts.AcceptsReport(tabIsActive: true, readerOpen: false));
        Assert.False(ReaderScripts.AcceptsReport(tabIsActive: false, readerOpen: true));
        Assert.True(ReaderScripts.AcceptsReport(tabIsActive: true, readerOpen: true));
        Assert.Contains("ReaderScripts.AcceptsReport(", System.IO.File.ReadAllText(System.IO.Path.Combine(Sync.GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "MainWindow.xaml.cs")));
    }

    [Fact]
    public void Detect_UsesFirefoxIsProbablyReaderable()
    {
        Assert.Contains("function isProbablyReaderable(", ReaderScripts.Detect);
        Assert.Contains("return isProbablyReaderable(document);", ReaderScripts.Detect);
    }

    // Proposta 3: o leitor nunca tem barra horizontal — o texto parte palavras compridas, as tabelas e os blocos de
    // código rolam por dentro, as figuras e a média cabem na coluna.
    [Fact]
    public void ReaderCss_HasNoHorizontalOverflow()
    {
        var css = ReaderCss();
        Assert.Matches(@"\.wrap \{[^}]*overflow-y: auto; overflow-x: hidden;", css);
        // Revisão: o .wrap corta o transbordo, por isso a barra (A−, A+, temas, serifa, Ouvir, velocidade, Sair) parte em
        // duas linhas numa janela estreita em vez de ficar com «Sair» fora de alcance.
        Assert.Matches(@"\.bar \{[^}]*display: flex; flex-wrap: wrap;", css);
        Assert.Contains(".content { overflow-wrap: anywhere; }", css);
        Assert.Contains(".content table { display: block; max-width: 100%; overflow-x: auto; }", css);
        Assert.Contains(".content figure, .content video, .content iframe, .content svg, .content canvas { max-width: 100%; height: auto; }", css);
        Assert.Matches(@"\.content pre \{[^}]*overflow-x: auto;", css);
    }

    // As barras do leitor: 10 px, sem setas, arredondadas, na cor do tema (--muted; --fg ao pairar), sem as propriedades
    // normalizadas que desligam o ::-webkit-scrollbar; e o leitor também respeita «reduzir movimento».
    [Fact]
    public void ReaderCss_HasThinThemedScrollbarsAndReducedMotion()
    {
        var css = ReaderCss();
        Assert.Contains("::-webkit-scrollbar { width: 10px; height: 10px; background: transparent; }", css);
        Assert.Contains("::-webkit-scrollbar-button { display: none; }", css);
        Assert.Contains("::-webkit-scrollbar-thumb { background: var(--muted); border-radius: 999px; border: 2px solid transparent; background-clip: padding-box; }", css);
        Assert.Contains("::-webkit-scrollbar-thumb:hover { background-color: var(--fg); }", css);
        Assert.DoesNotContain("scrollbar-color", css);
        Assert.DoesNotContain("scrollbar-width", css);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", css);
        Assert.Contains("behavior: still ? 'auto' : 'smooth'", ReaderScripts.Toggle);
    }

    private static string ReaderCss()
    {
        var script = ReaderScripts.Toggle;
        var start = script.IndexOf("const css = `", StringComparison.Ordinal);
        Assert.True(start > 0);
        return script[start..script.IndexOf('`', start + "const css = `".Length)];
    }
}
