using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests.Sync;

// A revisão final da 0.2.15, grupo «iu-textos» (final-review.md, I-01…I-12): o que as revisões por tarefa não viam na
// interface e nos textos públicos. Testes de fonte e de modelos; nada aqui abre janelas, toca na rede ou no perfil do dono.
public class FinalReviewUiTextsTests
{
    private static string Source(params string[] parts)
        => File.ReadAllText(Path.Combine([GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", .. parts])).Replace("\r\n", "\n");

    private static string Body(string text, string start, string end)
    {
        var at = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(at >= 0, "não encontrei " + start);
        var stop = text.IndexOf(end, at + start.Length, StringComparison.Ordinal);
        Assert.True(stop >= 0, "não encontrei o fim " + end);
        return text[at..stop];
    }

    // ---- I-01: as palavras do kit e uma seleção numa página interna não saem pelo menu ---------------------------------

    [Fact]
    public void I01_KitWords_CannotBeSelected()
    {
        var css = Regex.Match(Source("Browser", "settings.html"), @"\.kitwords \{[^}]*\}").Value;
        Assert.Contains("user-select: none", css);
    }

    [Fact]
    public void I01_SearchSelection_OnlyOnWebPages()
    {
        var inner = PageMenu.Build(new PageContext(null, null, "abacate afivelar", false, false, false, false, IsWebPage: false), "DuckDuckGo");
        Assert.DoesNotContain(inner, i => i?.Command == PageCommand.SearchSelection);
        var innerField = PageMenu.Build(new PageContext(null, null, "segredo", true, false, false, false, IsWebPage: false), "DuckDuckGo");
        Assert.DoesNotContain(innerField, i => i?.Command == PageCommand.SearchSelection);
        var web = PageMenu.Build(new PageContext(null, null, "abacate afivelar", false, false, false, false, IsWebPage: true), "DuckDuckGo");
        Assert.Contains(web, i => i?.Command == PageCommand.SearchSelection);
    }
}
