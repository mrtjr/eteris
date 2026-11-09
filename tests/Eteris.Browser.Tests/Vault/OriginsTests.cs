using Eteris.Browser.Vault;

namespace Eteris.Browser.Tests.Vault;

// A1 (docs/palavras-passe.md): a origem guardada e as regras de correspondência — exata, depois o mesmo domínio
// registável (marcado), nunca entre domínios; http e https separados.
public class OriginsTests
{
    [Theory]
    [InlineData("https://GitHub.com/login?x=1#y", "https://github.com")]
    [InlineData("https://github.com:443/", "https://github.com")]
    [InlineData("http://exemplo.test:80/a", "http://exemplo.test")]
    [InlineData("https://exemplo.test:8443/a", "https://exemplo.test:8443")]
    [InlineData("https://bücher.example/", "https://xn--bcher-kva.example")]
    [InlineData("https://user:pw@exemplo.test/", "https://exemplo.test")]
    [InlineData("http://127.0.0.1:8896/login", "http://127.0.0.1:8896")]
    [InlineData("http://[::1]:8080/", "http://[::1]:8080")]
    [InlineData("https://exemplo.test./x", "https://exemplo.test")]
    public void Normalize_KeepsSchemeHostAndPort(string url, string origin) => Assert.Equal(origin, Origins.Normalize(url));

    [Theory]
    [InlineData("eteris://settings/")]
    [InlineData("file:///C:/x.html")]
    [InlineData("data:text/html,x")]
    [InlineData("ftp://exemplo.test/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("não é um url")]
    [InlineData("")]
    public void Normalize_RefusesWhatIsNotAWebSite(string url) => Assert.Null(Origins.Normalize(url));

    // O Chrome exporta as palavras-passe das apps Android: guardam-se, mas nunca se sugerem.
    [Fact]
    public void Android_IsKeptButNeverSuggested()
    {
        const string app = "android://AbCdEf123==@com.exemplo.app/";
        Assert.Equal(app, Origins.Normalize(app));
        Assert.True(Origins.IsAndroid(app));
        Assert.Equal(OriginMatch.None, Origins.Match(app, "https://com.exemplo.app"));
        Assert.Equal(OriginMatch.None, Origins.Match(app, app));
    }

    [Fact]
    public void Match_ExactFirst_ThenSameRegistrableDomain()
    {
        Assert.Equal(OriginMatch.Exact, Origins.Match("https://github.com", "https://github.com"));
        Assert.Equal(OriginMatch.SameSite, Origins.Match("https://conta.exemplo.com", "https://www.exemplo.com"));
        Assert.Equal(OriginMatch.SameSite, Origins.Match("https://exemplo.com", "https://login.exemplo.com"));
        Assert.Equal(OriginMatch.None, Origins.Match("https://github.com", "https://g1thub.com"));
        Assert.Equal(OriginMatch.None, Origins.Match("https://github.com", "https://github.com.evil.test"));
    }

    [Fact]
    public void Match_HttpAndHttpsAreSeparate()
    {
        Assert.Equal(OriginMatch.None, Origins.Match("https://exemplo.com", "http://exemplo.com"));
        Assert.Equal(OriginMatch.None, Origins.Match("http://exemplo.com", "https://exemplo.com"));
        Assert.Equal(OriginMatch.Exact, Origins.Match("http://exemplo.com", "http://exemplo.com"));
    }

    // Cada um tem o seu site em github.io (sufixo público, secção PRIVATE da lista).
    [Fact]
    public void Match_PublicSuffixSubdomainsAreDifferentSites()
    {
        Assert.Equal(OriginMatch.None, Origins.Match("https://alice.github.io", "https://bob.github.io"));
        Assert.Equal(OriginMatch.Exact, Origins.Match("https://alice.github.io", "https://alice.github.io"));
    }

    [Fact]
    public void Match_PortsAndIpsMustBeTheSame()
    {
        Assert.Equal(OriginMatch.None, Origins.Match("https://exemplo.com:8443", "https://exemplo.com"));
        Assert.Equal(OriginMatch.None, Origins.Match("https://a.exemplo.com:8443", "https://b.exemplo.com"));
        Assert.Equal(OriginMatch.None, Origins.Match("http://127.0.0.1:8896", "http://127.0.0.2:8896"));
        Assert.Equal(OriginMatch.Exact, Origins.Match("http://127.0.0.1:8896", "http://127.0.0.1:8896"));
    }

    [Theory]
    [InlineData("https://exemplo.com", true)]
    [InlineData("http://exemplo.com", false)]
    [InlineData("http://localhost:3000", true)]
    [InlineData("http://app.localhost", true)]
    [InlineData("http://127.0.0.1:8896", true)]
    [InlineData("http://[::1]:8080", true)]
    [InlineData("http://192.168.1.1", false)]
    [InlineData("android://x@com.exemplo/", false)]
    public void IsSecure_HttpsAndTheLocalComputer(string origin, bool secure) => Assert.Equal(secure, Origins.IsSecure(origin));

    [Fact]
    public void SiteAndHost_ForDisplayAndTheSecondIndex()
    {
        Assert.Equal("exemplo.com", Origins.Site("https://conta.exemplo.com"));
        Assert.Equal("alice.github.io", Origins.Site("https://alice.github.io"));
        Assert.Equal("conta.exemplo.com", Origins.Host("https://conta.exemplo.com:8443"));
        Assert.Equal("bücher.example", Origins.DisplayHost("https://xn--bcher-kva.example"));
        Assert.Null(Origins.Site("android://x@com.exemplo/"));
    }
}
