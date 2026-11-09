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

    // 0.2.13: abre com a conta Google e a sincronização cifrada, as palavras-passe e o preenchimento, e o pedido à
    // Google dos formulários cortado (3 destaques); a seguir vêm os 2 da 0.2.12, a abrir com o teclado do menu (Alt+F e
    // Alt+E) e o menu legível, os 2 da 0.2.11, a abrir com o arranque sem pedidos à Google e o Escudo nos pedidos sem
    // aba, os 5 da 0.2.10, a abrir com o arranque e a memória, seguidos da impressão digital, e depois os 8 da 0.2.9, a
    // abrir com o vídeo no motor principal (codecs do Windows). O destaque antigo que mandava o H.264 para o motor de
    // vídeo saiu, e os destaques das cinco versões são curtos.
    [Fact]
    public void Highlights_LeadWithSyncPasswordsAndFormsThenTheMenuKeyboardThenNothingToGoogleThenStartupThenTheEngineVideo_AndAreShort()
    {
        Assert.Equal("Conta Google e sincronização cifrada", WhatsNewPage.Highlights[0].Title);
        Assert.Contains("Opcional", WhatsNewPage.Highlights[0].Detail);
        Assert.Contains("cifrados no PC", WhatsNewPage.Highlights[0].Detail);
        Assert.Contains("pasta oculta", WhatsNewPage.Highlights[0].Detail);
        Assert.Contains("palavras-passe", WhatsNewPage.Highlights[0].Detail);
        Assert.Equal("Palavras-passe e preenchimento", WhatsNewPage.Highlights[1].Title);
        Assert.Contains("nunca o CVV", WhatsNewPage.Highlights[1].Detail);
        Assert.Contains("Windows Hello", WhatsNewPage.Highlights[1].Detail);
        Assert.Equal("Formulários sem pedidos à Google", WhatsNewPage.Highlights[2].Title);
        Assert.Contains("verificação de fugas", WhatsNewPage.Highlights[2].Detail);
        Assert.Contains("0 pedidos", WhatsNewPage.Highlights[2].Detail);
        Assert.Equal("Menu do Éteris pelo teclado", WhatsNewPage.Highlights[3].Title);
        Assert.Equal("Alt+F · Alt+E", WhatsNewPage.Highlights[3].Keys);
        Assert.Contains("Esc", WhatsNewPage.Highlights[3].Detail);
        Assert.Equal("Menu legível", WhatsNewPage.Highlights[4].Title);
        Assert.Contains("quadrados", WhatsNewPage.Highlights[4].Detail);
        Assert.Equal("No arranque, nada sai para a Google", WhatsNewPage.Highlights[5].Title);
        Assert.Contains("eteris://privacy/", WhatsNewPage.Highlights[5].Detail);
        Assert.Contains("service workers", WhatsNewPage.Highlights[6].Detail);
        Assert.Equal("Arranque quase duas vezes mais rápido", WhatsNewPage.Highlights[7].Title);
        Assert.Contains("0,6 s", WhatsNewPage.Highlights[7].Detail);
        Assert.Equal("Menos memória", WhatsNewPage.Highlights[8].Title);
        Assert.Contains(WhatsNewPage.Highlights.Skip(7).Take(5), h => h.Title == "Impressão digital como a do Chrome");
        Assert.Equal("Vídeo no motor do Éteris", WhatsNewPage.Highlights[12].Title);
        Assert.DoesNotContain(WhatsNewPage.Highlights, h => h.Title == "Vídeo em todos os sites");
        Assert.All(WhatsNewPage.Highlights.Take(20), h => Assert.True(h.Detail.Length <= 180, h.Title));
    }

    // O sandbox está pronto mas desligado na 0.2.10, na 0.2.11, na 0.2.12 e na 0.2.13: as Novidades não o anunciam antes de ligar.
    [Fact]
    public void Highlights_DoNotAnnounceTheSandboxBeforeItIsOn()
        => Assert.DoesNotContain(WhatsNewPage.Highlights, h => h.Title.Contains("sandbox", StringComparison.OrdinalIgnoreCase) || h.Detail.Contains("sandbox", StringComparison.OrdinalIgnoreCase));

    // Os fundos são imagens geradas por IA (Browser/Wallpapers/README.md): as novidades não lhes chamam fotografias,
    // para dizer o mesmo que o crédito no rodapé da nova aba.
    [Fact]
    public void Highlights_CallTheWallpapersAiGeneratedImages()
    {
        Assert.Contains(WhatsNewPage.Highlights, h => h.Detail.Contains("gerada por IA", StringComparison.Ordinal));
        Assert.DoesNotContain(WhatsNewPage.Highlights, h => h.Detail.Contains("fotografia", StringComparison.OrdinalIgnoreCase));
    }
}
