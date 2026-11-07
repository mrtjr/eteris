using System.Text.Json;
using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

// Correções da revisão do bloco A no «Espreitar»: corrida entre dois pedidos, Alt+clique nas páginas internas, nos
// sites fixados e em iframes, links para ficheiros, descrições gigantes e rolagem pelo teclado.
public class ReviewGlanceTests
{
    [Fact]
    public void TwoRequests_TheFirstDocumentFinishingLate_IsNotShownUnderTheSecond()
    {
        var loads = new GlanceLoads();
        var serial = 1;                     // Alt+clique em A
        loads.Committed(serial);            // A confirmado
        serial = 2;                         // Alt+clique em B antes de A acabar
        var finishedA = loads.Finished();   // o fim de A chega depois
        Assert.Equal(1, finishedA);
        Assert.NotEqual(serial, finishedA);
        loads.Committed(serial);            // B confirmado (também depois de redirecionamentos)
        Assert.Equal(2, loads.Finished());
    }

    [Fact]
    public void NoCommittedDocument_FinishesNothing() => Assert.Null(new GlanceLoads().Finished());

    [Theory]
    [InlineData("eteris://newtab", true)]
    [InlineData("eteris://bookmarks", true)]
    [InlineData("https://exemplo.pt/", true)]
    [InlineData("http://exemplo.pt/", true)]
    [InlineData("file:///C:/x.html", false)]
    [InlineData("about:blank", false)]
    [InlineData(null, false)]
    public void AltClickScript_AlsoOnInternalPages(string? url, bool expected) => Assert.Equal(expected, LinkPreview.WantsAltClick(url));

    [Fact]
    public void AltClickDownload_IsDivertedOnlyWithAltAndAWebLink()
    {
        Assert.True(LinkPreview.DivertsDownload(true, "https://exemplo.pt/artigo"));
        Assert.False(LinkPreview.DivertsDownload(false, "https://exemplo.pt/artigo"));
        Assert.False(LinkPreview.DivertsDownload(true, "blob:https://exemplo.pt/1"));
        Assert.False(LinkPreview.DivertsDownload(true, null));
    }

    [Fact]
    public void FileStatus_SaysItIsAFile() => Assert.Contains("ficheiro", LinkPreview.FileStatus);

    [Fact]
    public void HugeDescription_IsCutOnAWord()
    {
        var article = string.Join(' ', Enumerable.Repeat("palavra", 400));
        var head = $"<head><meta property=\"og:description\" content=\"{article}\"><meta property=\"og:title\" content=\"Título\"></head>";
        var json = JsonSerializer.Serialize(new { head, title = "", byline = "", site = "", text = "", words = 0 });
        var info = LinkPreview.Parse(json, "https://exemplo.pt/a")!;
        Assert.NotNull(info.Description);
        Assert.True(info.Description!.Length <= LinkPreview.MaxDescriptionChars + 1);
        Assert.EndsWith("palavra…", info.Description);
        // Uma descrição curta fica inteira.
        var shortJson = JsonSerializer.Serialize(new { head = "<meta name=\"description\" content=\"Curta.\">", text = "" });
        Assert.Equal("Curta.", LinkPreview.Parse(shortJson, "https://exemplo.pt/a")!.Description);
    }

    [Theory]
    [InlineData("Down", false, GlanceScroll.LineDown)]
    [InlineData("Up", false, GlanceScroll.LineUp)]
    [InlineData("PageDown", false, GlanceScroll.PageDown)]
    [InlineData("Next", false, GlanceScroll.PageDown)]
    [InlineData("PageUp", false, GlanceScroll.PageUp)]
    [InlineData("Space", false, GlanceScroll.PageDown)]
    [InlineData("Space", true, GlanceScroll.PageUp)]
    [InlineData("Home", false, GlanceScroll.Top)]
    [InlineData("End", false, GlanceScroll.Bottom)]
    [InlineData("A", false, GlanceScroll.None)]
    [InlineData("Escape", false, GlanceScroll.None)]
    public void Keyboard_ScrollsTheCard(string key, bool shift, GlanceScroll expected) => Assert.Equal(expected, LinkPreview.ScrollFor(key, shift));
}
