using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// A casa de uma aba fixada (Tabs/PinnedHome.cs), sem WPF: o que pode ser casa, o que é «a mesma casa», o rótulo, a adoção
// da 1.ª página web, o redirecionamento aceite ao voltar e a leitura do ficheiro da sessão.
public class PinnedHomeTests
{
    [Fact]
    public void Same_IgnoresTheFragment()
        => Assert.True(PinnedHome.Same("https://exemplo.pt/a#b", "https://exemplo.pt/a"));

    [Fact]
    public void Same_NormalizesTheTrailingSlash()
    {
        Assert.True(PinnedHome.Same("https://exemplo.pt/caixa/", "https://exemplo.pt/caixa"));
        Assert.True(PinnedHome.Same("https://exemplo.pt", "https://exemplo.pt/"));
    }

    [Fact]
    public void Same_IgnoresSchemeAndHostCase()
        => Assert.True(PinnedHome.Same("HTTPS://Exemplo.PT/a", "https://exemplo.pt/a"));

    [Fact]
    public void Same_KeepsThePathCase()
        => Assert.False(PinnedHome.Same("https://exemplo.pt/A", "https://exemplo.pt/a"));

    [Fact]
    public void Same_KeepsTheQuery_WithoutTrackingParameters()
    {
        Assert.False(PinnedHome.Same("https://exemplo.pt/ver?v=1", "https://exemplo.pt/ver?v=2"));
        Assert.True(PinnedHome.Same("https://exemplo.pt/a?utm_source=x&fbclid=y", "https://exemplo.pt/a"));
        Assert.True(PinnedHome.Same("https://exemplo.pt/a?", "https://exemplo.pt/a"));
        Assert.False(PinnedHome.Same("https://exemplo.pt/a?a=1&b=2", "https://exemplo.pt/a?b=2&a=1"));
    }

    [Fact]
    public void Same_IgnoresDefaultPortAndUserInfo()
        => Assert.True(PinnedHome.Same("https://ana:segredo@exemplo.pt:443/a", "https://exemplo.pt/a"));

    [Fact]
    public void Same_DistinguishesSchemeAndWww()
    {
        Assert.False(PinnedHome.Same("http://exemplo.pt/a", "https://exemplo.pt/a"));
        Assert.False(PinnedHome.Same("https://www.exemplo.pt/a", "https://exemplo.pt/a"));
        Assert.False(PinnedHome.Same("https://exemplo.pt:8443/a", "https://exemplo.pt/a"));
    }

    [Fact]
    public void Same_IsNeverTrueWithoutAHome()
    {
        Assert.False(PinnedHome.Same(null, "https://exemplo.pt/"));
        Assert.False(PinnedHome.Same("eteris://newtab/", "eteris://newtab/"));
    }

    [Theory]
    [InlineData("eteris://newtab/", false)]
    [InlineData("eteris://settings/", false)]
    [InlineData("file:///C:/x.html", false)]
    [InlineData("data:text/html,x", false)]
    [InlineData("about:blank", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("https://exemplo.pt/", true)]
    [InlineData("http://exemplo.pt/", true)]
    public void CanBeHome_OnlyWebAddresses(string? address, bool expected)
        => Assert.Equal(expected, PinnedHome.CanBeHome(address));

    [Fact]
    public void Clean_DropsUserInfo_KeepsTheFragment()
    {
        Assert.Equal("https://exemplo.pt/a#b", PinnedHome.Clean("https://ana:segredo@exemplo.pt/a#b"));
        Assert.Equal("https://exemplo.pt/a?x=1", PinnedHome.Clean("  https://exemplo.pt/a?x=1 "));
        Assert.Null(PinnedHome.Clean("eteris://newtab/"));
    }

    [Fact]
    public void Label_ShowsTheHostAndAShortPath()
    {
        Assert.Equal("exemplo.pt", PinnedHome.Label("https://www.exemplo.pt/"));
        Assert.Equal("exemplo.pt/caixa/entrada", PinnedHome.Label("https://exemplo.pt/caixa/entrada?x=1#y"));
        Assert.Equal("pt.wikipedia.org/wiki/Lisboa", PinnedHome.Label("https://pt.wikipedia.org/wiki/Lisboa"));
        var longLabel = PinnedHome.Label("https://exemplo.pt/" + new string('a', 80));
        Assert.True(longLabel.Length <= 40, longLabel);
        Assert.EndsWith("…", longLabel, StringComparison.Ordinal);
        Assert.StartsWith("exemplo.pt/aaa", longLabel, StringComparison.Ordinal);
        // Legível, sem os caracteres de direção do texto.
        Assert.Equal("pt.wikipedia.org/wiki/São_Paulo", PinnedHome.Label("https://pt.wikipedia.org/wiki/S%C3%A3o_Paulo"));
        Assert.Equal("exemplo.pt/ab", PinnedHome.Label("https://exemplo.pt/a%E2%80%AEb"));
        Assert.Equal("exemplo.pt:8443", PinnedHome.Label("https://exemplo.pt:8443/"));
    }

    [Fact]
    public void Adopt_OnlyWhenPinnedWithoutHome()
    {
        Assert.Null(PinnedHome.Adopt(pinned: false, home: null, "https://a.pt/"));
        Assert.Equal("https://b.pt/", PinnedHome.Adopt(pinned: true, home: "https://b.pt/", "https://a.pt/"));
        Assert.Equal("https://a.pt/x", PinnedHome.Adopt(pinned: true, home: null, "https://ana:segredo@a.pt/x"));
        Assert.Null(PinnedHome.Adopt(pinned: true, home: null, "eteris://history/"));
    }

    [Theory]
    [InlineData("http://exemplo.pt/a", "https://exemplo.pt/a", true)]
    [InlineData("https://exemplo.pt/a", "https://www.exemplo.pt/a", true)]
    [InlineData("https://www.exemplo.pt/a", "https://exemplo.pt/a", true)]
    [InlineData("http://exemplo.pt/", "https://www.exemplo.pt/", true)]
    [InlineData("https://exemplo.pt/a", "http://exemplo.pt/a", false)]
    [InlineData("https://exemplo.pt/a", "https://exemplo.pt/inicio", false)]
    [InlineData("https://app.exemplo.pt/painel", "https://app.exemplo.pt/login?next=%2Fpainel", false)]
    [InlineData("https://exemplo.pt/", "https://contas.exemplo.com/", false)]
    [InlineData("https://exemplo.pt/a", "https://exemplo.pt:8443/a", false)]
    [InlineData("https://exemplo.pt/a", "https://www.www.exemplo.pt/a", false)]
    [InlineData("https://exemplo.pt/a", "eteris://newtab/", false)]
    public void IsRedirectOfHome(string home, string landed, bool expected)
        => Assert.Equal(expected, PinnedHome.IsRedirectOfHome(home, landed));

    [Fact]
    public void Restore_FallsBackToTheAddress()
    {
        Assert.Equal("https://a.pt/x", PinnedHome.Restore(null, "https://a.pt/x"));
        Assert.Equal("https://a.pt/", PinnedHome.Restore("javascript:x", "https://a.pt/"));
        Assert.Null(PinnedHome.Restore(null, "eteris://newtab/"));
        Assert.Equal("https://b.pt/", PinnedHome.Restore("https://b.pt/", "https://a.pt/"));
        Assert.Equal("https://b.pt/", PinnedHome.Restore("https://ana:segredo@b.pt/", "https://a.pt/"));
    }
}
