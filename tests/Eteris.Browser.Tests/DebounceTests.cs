using Eteris.Browser.Data;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

// B7 «Saltar redirecionamentos de rastreio»: o destino que vem no próprio endereço do salto.
public class DebounceTests
{
    [Theory]
    [InlineData("https://l.facebook.com/l.php?u=https%3A%2F%2Fwww.publico.pt%2Fartigo%3Fid%3D1&h=AT0abc", "https://www.publico.pt/artigo?id=1")]
    [InlineData("https://lm.facebook.com/l.php?u=https%3A%2F%2Fexample.com%2F", "https://example.com/")]
    [InlineData("https://www.google.com/url?sa=t&url=https%3A%2F%2Fpt.wikipedia.org%2Fwiki%2FLisboa&usg=AOv", "https://pt.wikipedia.org/wiki/Lisboa")]
    [InlineData("https://www.google.pt/url?q=https://example.com/a&sa=D", "https://example.com/a")]
    [InlineData("https://www.google.com.br/url?q=https%3A%2F%2Fexample.com%2F", "https://example.com/")]
    [InlineData("https://eur01.safelinks.protection.outlook.com/?url=https%3A%2F%2Fexample.com%2Fx&data=05", "https://example.com/x")]
    [InlineData("https://www.youtube.com/redirect?event=video_description&q=https%3A%2F%2Fexample.com%2F", "https://example.com/")]
    [InlineData("https://out.reddit.com/t3_abc?url=https%3A%2F%2Fexample.com%2F&token=1", "https://example.com/")]
    [InlineData("https://steamcommunity.com/linkfilter/?u=https%3A%2F%2Fexample.com%2F", "https://example.com/")]
    [InlineData("https://www.linkedin.com/safety/go?url=https%3A%2F%2Fexample.com%2F&trk=x", "https://example.com/")]
    [InlineData("https://vk.com/away.php?to=https%3A%2F%2Fexample.com%2F", "https://example.com/")]
    [InlineData("https://duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2F&rut=1", "https://example.com/")]
    [InlineData("https://www.googleadservices.com/pagead/aclk?sa=L&adurl=https%3A%2F%2Floja.pt%2F", "https://loja.pt/")]
    [InlineData("https://www.bing.com/ck/a?!&&p=abc&u=a1aHR0cHM6Ly9leGFtcGxlLmNvbS9wYWdpbmE&ntb=1", "https://example.com/pagina")]
    public void KnownRedirectors_GoStraightToTheDestination(string link, string destination)
        => Assert.Equal(destination, Debounce.Destination(link));

    [Fact]
    public void DoubleEncodedDestination_IsDecodedOnce()
        => Assert.Equal("https://example.com/a?b=1",
            Debounce.Destination("https://l.facebook.com/l.php?u=https%253A%252F%252Fexample.com%252Fa%253Fb%253D1"));

    [Fact]
    public void ChainedRedirectors_AreAllSkipped()
    {
        var inner = "https://l.facebook.com/l.php?u=" + Uri.EscapeDataString("https://example.com/fim");
        var outer = "https://www.google.com/url?q=" + Uri.EscapeDataString(inner);
        Assert.Equal("https://example.com/fim", Debounce.Destination(outer));
        Assert.Equal("www.google.com", Debounce.RedirectorHost(outer));
    }

    [Theory]
    // Sem destino no endereço, com destino que não é web, com credenciais ou em sítios que não são saltos.
    [InlineData("https://l.facebook.com/l.php?h=AT0abc")]
    [InlineData("https://l.facebook.com/l.php?u=javascript%3Aalert(1)")]
    [InlineData("https://l.facebook.com/l.php?u=file%3A%2F%2F%2FC%3A%2Fwindows")]
    [InlineData("https://l.facebook.com/l.php?u=data%3Atext%2Fhtml%2C%3Cb%3Eoi")]
    [InlineData("https://l.facebook.com/l.php?u=https%3A%2F%2Fbanco.pt%40outro.site%2F")]
    [InlineData("https://l.facebook.com/l.php?u=%2Fpagina-relativa")]
    [InlineData("https://www.facebook.com/l.php?u=https%3A%2F%2Fexample.com%2F")]
    [InlineData("https://www.google.com/search?q=https%3A%2F%2Fexample.com%2F")]
    [InlineData("https://google.example.com/url?q=https%3A%2F%2Fexample.com%2F")]
    [InlineData("https://accounts.google.com/ServiceLogin?continue=https%3A%2F%2Fmail.google.com%2F")]
    [InlineData("https://www.bing.com/ck/a?u=aHR0cHM6Ly9leGFtcGxlLmNvbQ")]
    [InlineData("https://www.bing.com/ck/a?u=a1%%%")]
    [InlineData("https://example.com/?url=https%3A%2F%2Foutro.pt%2F")]
    [InlineData("eteris://settings/?url=https://example.com/")]
    [InlineData("not a url")]
    public void OtherLinks_AreLeftAlone(string link)
        => Assert.Null(Debounce.Destination(link));

    [Fact]
    public void TrackerHosts_UseTheObviousParameters()
    {
        static bool tracker(string host) => host.EndsWith("list-manage.com", StringComparison.Ordinal);
        Assert.Equal("https://loja.pt/promo",
            Debounce.Destination("https://news.us1.list-manage.com/track/click?u=1&id=2&url=https%3A%2F%2Floja.pt%2Fpromo", tracker));
        Assert.Equal("https://loja.pt/", Debounce.Destination("https://news.us1.list-manage.com/c?destination=https://loja.pt/", tracker));
        // Os mesmos parâmetros num site que não é rastreador ficam como estão (um «?redirect=» de um login, por exemplo).
        Assert.Null(Debounce.Destination("https://loja.pt/entrar?redirect=https%3A%2F%2Floja.pt%2Fconta", tracker));
        // redirect_uri é OAuth: nunca é um salto.
        Assert.Null(Debounce.Destination("https://news.us1.list-manage.com/auth?redirect_uri=https%3A%2F%2Floja.pt%2F", tracker));
    }

    [Fact]
    public void MinimalTrackerList_KnowsSubdomains()
    {
        Assert.True(TrackerBlocker.Default.Lists("stats.g.doubleclick.net"));
        Assert.False(TrackerBlocker.Default.Lists("pt.wikipedia.org"));
    }

    [Fact]
    public void ShieldLine_SaysHowManyAndWhere()
    {
        Assert.Equal("✓  Os links que passam por rastreadores vão direto ao destino", DebounceText.ShieldLine([], enabled: true));
        Assert.Equal("✓  1 salto de rastreio removido: l.facebook.com", DebounceText.ShieldLine(["l.facebook.com"], enabled: true));
        Assert.Equal("✓  3 saltos de rastreio removidos: www.google.com e mais 1",
            DebounceText.ShieldLine(["www.google.com", "l.facebook.com", "www.google.com"], enabled: true));
        Assert.StartsWith("·", DebounceText.ShieldLine(["l.facebook.com"], enabled: false));
        Assert.Equal(" · 1 salto de rastreio removido", DebounceText.Tooltip(1));
        Assert.Equal(string.Empty, DebounceText.Tooltip(0));
    }

    [Fact]
    public void Setting_IsOnByDefaultAndCanBeTurnedOff()
    {
        Assert.True(new UserSettings().SkipTrackingRedirects);
        Assert.False(new UserSettings().With("debounce", "false")!.SkipTrackingRedirects);
    }
}
