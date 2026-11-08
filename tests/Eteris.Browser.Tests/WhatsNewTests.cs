using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

public class WhatsNewTests
{
    [Theory]
    [InlineData("0.1.0", "0.2.0", false, true)]
    [InlineData("0.2.0", "0.2.0", false, false)]
    [InlineData("", "0.2.0", true, false)]
    public void ShowsOncePerVersionButNotOnFirstRun(string seen, string current, bool firstRun, bool expected)
        => Assert.Equal(expected, WhatsNewPage.ShouldShow(seen, current, firstRun));

    [Fact]
    public void Render_ListsHighlights()
    {
        var html = WhatsNewPage.Render("0.2.0");
        Assert.Contains("\"version\":\"0.2.0\"", html);
        Assert.True(WhatsNewPage.Highlights.Count >= 8);
    }

    // 0.2.9: abre com o vídeo no motor principal (codecs do Windows); o destaque antigo que mandava o H.264 para o
    // motor de vídeo saiu, e os destaques da versão são curtos.
    [Fact]
    public void Highlights_LeadWithTheEngineVideo_AndAreShort()
    {
        Assert.Equal("Vídeo no motor do Éteris", WhatsNewPage.Highlights[0].Title);
        Assert.DoesNotContain(WhatsNewPage.Highlights, h => h.Title == "Vídeo em todos os sites");
        Assert.All(WhatsNewPage.Highlights.Take(8), h => Assert.True(h.Detail.Length <= 180, h.Title));
    }

    // Os fundos são imagens geradas por IA (Browser/Wallpapers/README.md): as novidades não lhes chamam fotografias,
    // para dizer o mesmo que o crédito no rodapé da nova aba.
    [Fact]
    public void Highlights_CallTheWallpapersAiGeneratedImages()
    {
        Assert.Contains(WhatsNewPage.Highlights, h => h.Detail.Contains("gerada por IA", StringComparison.Ordinal));
        Assert.DoesNotContain(WhatsNewPage.Highlights, h => h.Detail.Contains("fotografia", StringComparison.OrdinalIgnoreCase));
    }
}
