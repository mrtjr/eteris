using System.IO;
using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

public class WhatsNewTests
{
    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

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

    // 0.2.14: abre com o isolamento das páginas (1 destaque, por agora só o do sandbox). A seguir vêm os 2 da 0.2.12,
    // a abrir com o teclado do menu (Alt+F e Alt+E) e o menu legível; os 2 da 0.2.11, a abrir com o arranque sem pedidos
    // à Google e o Escudo nos pedidos sem aba; os 5 da 0.2.10, a abrir com o arranque e a memória, seguidos da impressão
    // digital; e depois os 8 da 0.2.9, a abrir com o vídeo no motor principal (codecs do Windows). O destaque antigo que
    // mandava o H.264 para o motor de vídeo saiu, e os destaques das cinco versões são curtos.
    // Ao fundir a 0.2.13, os destaques dela entram entre o da 0.2.14 e os da 0.2.12, e os índices daqui andam com eles.
    [Fact]
    public void Highlights_LeadWithTheSandboxThenTheMenuKeyboardThenNothingToGoogleThenStartupThenTheEngineVideo_AndAreShort()
    {
        Assert.Equal(WhatsNewPage.SandboxTitle, WhatsNewPage.Highlights[0].Title);
        Assert.Equal("Menu do Éteris pelo teclado", WhatsNewPage.Highlights[1].Title);
        Assert.Equal("Alt+F · Alt+E", WhatsNewPage.Highlights[1].Keys);
        Assert.Contains("Esc", WhatsNewPage.Highlights[1].Detail);
        Assert.Equal("Menu legível", WhatsNewPage.Highlights[2].Title);
        Assert.Contains("quadrados", WhatsNewPage.Highlights[2].Detail);
        Assert.Equal("No arranque, nada sai para a Google", WhatsNewPage.Highlights[3].Title);
        Assert.Contains("eteris://privacy/", WhatsNewPage.Highlights[3].Detail);
        Assert.Contains("service workers", WhatsNewPage.Highlights[4].Detail);
        Assert.Equal("Arranque quase duas vezes mais rápido", WhatsNewPage.Highlights[5].Title);
        Assert.Contains("0,6 s", WhatsNewPage.Highlights[5].Detail);
        Assert.Equal("Menos memória", WhatsNewPage.Highlights[6].Title);
        Assert.Contains(WhatsNewPage.Highlights.Skip(5).Take(5), h => h.Title == "Impressão digital como a do Chrome");
        Assert.Equal("Vídeo no motor do Éteris", WhatsNewPage.Highlights[10].Title);
        Assert.DoesNotContain(WhatsNewPage.Highlights, h => h.Title == "Vídeo em todos os sites");
        Assert.All(WhatsNewPage.Highlights.Take(18), h => Assert.True(h.Detail.Length <= 180, h.Title));
    }

    // Desligado da 0.2.10 à 0.2.13, o sandbox sai ligado na publicação da 0.2.14 (docs/sandbox.md, «Como publicar com o
    // sandbox»): as Novidades abrem com ele, num destaque curto, e é o único que fala dele.
    [Fact]
    public void Highlights_AnnounceTheSandboxIn0214()
    {
        var first = WhatsNewPage.Highlights[0];
        Assert.Equal(WhatsNewPage.SandboxTitle, first.Title);
        Assert.Contains("sandbox", first.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Chrome", first.Detail);
        Assert.Contains("ficheiros", first.Detail);
        Assert.True(first.Detail.Length <= 180, first.Detail);
        Assert.Single(WhatsNewPage.Highlights, h => h.Title.Contains("sandbox", StringComparison.OrdinalIgnoreCase)
                                                    || h.Detail.Contains("sandbox", StringComparison.OrdinalIgnoreCase)
                                                    || h.Title.Contains("isolad", StringComparison.OrdinalIgnoreCase));
    }

    // As Novidades não podem anunciar o isolamento numa publicação sem ele: o anúncio segue a omissão do
    // build/publish.ps1. Uma versão de recuo sem sandbox (por exemplo uma 0.2.14.1 com -NoSandbox) muda a omissão do
    // publish.ps1, e este teste obriga então a tirar o anúncio (docs/sandbox.md; plano da 0.2.14, recuo R3).
    [Fact]
    public void Highlights_AnnounceTheSandboxOnlyWhenThePublishDefaultIsSandbox()
    {
        var param = SandboxPublishTests.PublishParam(File.ReadAllText(Path.Combine(Root(), "build", "publish.ps1")));
        var announced = WhatsNewPage.Highlights.Any(h => h.Title == WhatsNewPage.SandboxTitle);
        Assert.Equal(SandboxPublishTests.DefaultIsSandbox(param), announced);
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
