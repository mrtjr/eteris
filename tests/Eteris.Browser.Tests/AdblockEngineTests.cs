using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

public sealed class AdblockEngineTests : IDisposable
{
    private readonly AdblockEngine _engine = AdblockEngine.Create("""
        ||doubleclick.net^
        @@||doubleclick.net/allowed/$script
        publico.pt##.ad-slot
        ##.banner-publicidade
        """);

    public void Dispose() => _engine.Dispose();

    [Fact]
    public void ShouldBlock_ListedTracker()
        => Assert.True(_engine.ShouldBlock("https://ad.doubleclick.net/x.js", "https://www.publico.pt/", "script"));

    [Fact]
    public void ShouldBlock_ExceptionRuleWins()
        => Assert.False(_engine.ShouldBlock("https://ad.doubleclick.net/allowed/x.js", "https://www.publico.pt/", "script"));

    [Fact]
    public void ShouldBlock_OrdinaryRequestPasses()
        => Assert.False(_engine.ShouldBlock("https://pt.wikipedia.org/wiki/Lisboa", "https://pt.wikipedia.org/", "document"));

    [Fact]
    public void ShouldBlock_WithoutPage_UsesRequestAsSource()
        => Assert.True(_engine.ShouldBlock("https://ad.doubleclick.net/x.js", null, "script"));

    [Fact]
    public void ShouldBlock_NonAsciiUrlsCrossTheBoundary()
        => Assert.False(_engine.ShouldBlock("https://pt.wikipedia.org/wiki/Página_principal", "https://pt.wikipedia.org/", "document"));

    [Fact]
    public void UrlCss_ReturnsSiteSpecificRules()
        => Assert.Equal(".ad-slot{display:none!important}\n", _engine.UrlCss("https://www.publico.pt/"));

    [Fact]
    public void GenericCss_MatchesClassesPresentOnPage()
        => Assert.Equal(".banner-publicidade{display:none!important}\n",
            _engine.GenericCss("https://www.publico.pt/", ["banner-publicidade", "artigo"], []));

    [Fact]
    public void Dispose_IsIdempotentAndBlocksNothingAfterwards()
    {
        var engine = AdblockEngine.Create("||doubleclick.net^");
        engine.Dispose();
        engine.Dispose();
        Assert.False(engine.ShouldBlock("https://ad.doubleclick.net/x.js", null, "script"));
        Assert.Equal("", engine.UrlCss("https://www.publico.pt/"));
    }
}
