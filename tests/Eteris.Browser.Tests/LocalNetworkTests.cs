using CefSharp;
using Eteris.Browser.Permissions;
using Eteris.Browser.Shields;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// B6 «Permissão para a rede local»: classificação dos endereços e política por site.
public class LocalNetworkTests
{
    [Theory]
    [InlineData("127.0.0.1", LocalNetwork.Space.Loopback)]
    [InlineData("127.8.9.10", LocalNetwork.Space.Loopback)]
    [InlineData("0.0.0.0", LocalNetwork.Space.Loopback)]
    [InlineData("localhost", LocalNetwork.Space.Loopback)]
    [InlineData("LOCALHOST.", LocalNetwork.Space.Loopback)]
    [InlineData("app.localhost", LocalNetwork.Space.Loopback)]
    [InlineData("::1", LocalNetwork.Space.Loopback)]
    [InlineData("[::1]", LocalNetwork.Space.Loopback)]
    [InlineData("::ffff:127.0.0.1", LocalNetwork.Space.Loopback)]
    [InlineData("10.0.0.1", LocalNetwork.Space.Private)]
    [InlineData("172.16.0.1", LocalNetwork.Space.Private)]
    [InlineData("172.31.255.254", LocalNetwork.Space.Private)]
    [InlineData("192.168.1.1", LocalNetwork.Space.Private)]
    [InlineData("169.254.169.254", LocalNetwork.Space.Private)]
    [InlineData("fc00::1", LocalNetwork.Space.Private)]
    [InlineData("fd12:3456::1", LocalNetwork.Space.Private)]
    [InlineData("fe80::1", LocalNetwork.Space.Private)]
    [InlineData("::ffff:192.168.0.10", LocalNetwork.Space.Private)]
    [InlineData("impressora.local", LocalNetwork.Space.Private)]
    [InlineData("nas.home.arpa", LocalNetwork.Space.Private)]
    [InlineData("servidor.internal", LocalNetwork.Space.Private)]
    [InlineData("router", LocalNetwork.Space.Private)]
    [InlineData("172.15.0.1", LocalNetwork.Space.Public)]
    [InlineData("172.32.0.1", LocalNetwork.Space.Public)]
    [InlineData("8.8.8.8", LocalNetwork.Space.Public)]
    [InlineData("192.169.1.1", LocalNetwork.Space.Public)]
    [InlineData("2001:4860:4860::8888", LocalNetwork.Space.Public)]
    [InlineData("pt.wikipedia.org", LocalNetwork.Space.Public)]
    [InlineData("localhost.exemplo.pt", LocalNetwork.Space.Public)]
    [InlineData("local.pt", LocalNetwork.Space.Public)]
    [InlineData("", LocalNetwork.Space.Public)]
    public void Classify_TellsThisComputerTheHomeNetworkAndTheInternetApart(string host, LocalNetwork.Space expected)
        => Assert.Equal(expected, LocalNetwork.Classify(host));

    [Theory]
    // Uma página pública não chega a este computador nem à rede de casa: fetch, XHR, imagens, iframes, WebSockets.
    [InlineData("https://www.exemplo.pt/", "http://127.0.0.1:6463/rpc", ResourceType.Xhr, false, LocalNetwork.Decision.Block)]
    [InlineData("http://lan-teste.example:8765/lan.html", "http://localhost:8766/echo", ResourceType.SubFrame, false, LocalNetwork.Decision.Block)]
    [InlineData("https://www.exemplo.pt/", "http://192.168.1.1/admin", ResourceType.Image, false, LocalNetwork.Decision.Block)]
    [InlineData("https://www.exemplo.pt/", "ws://127.0.0.1:9222/devtools", ResourceType.Xhr, false, LocalNetwork.Decision.Block)]
    [InlineData("https://www.exemplo.pt/", "http://impressora.local/", ResourceType.Script, false, LocalNetwork.Decision.Block)]
    // Com autorização para o site, passa.
    [InlineData("https://www.exemplo.pt/", "http://127.0.0.1:6463/rpc", ResourceType.Xhr, true, LocalNetwork.Decision.Allow)]
    // A navegação de topo para um endereço local é livre (escrever 192.168.1.1 na barra de endereço).
    [InlineData("https://www.exemplo.pt/", "http://192.168.1.1/", ResourceType.MainFrame, false, LocalNetwork.Decision.Allow)]
    // Páginas locais falam com o que é local (o servidor de teste, o painel do router).
    [InlineData("http://127.0.0.1:8765/rede.html", "http://localhost:8766/echo", ResourceType.SubFrame, false, LocalNetwork.Decision.Allow)]
    [InlineData("http://192.168.1.1/", "http://192.168.1.1/estilo.css", ResourceType.Stylesheet, false, LocalNetwork.Decision.Allow)]
    // Internet → Internet, páginas internas e endereços que não são da Web: nada a ver.
    [InlineData("https://www.exemplo.pt/", "https://cdn.exemplo.pt/a.js", ResourceType.Script, false, LocalNetwork.Decision.Allow)]
    [InlineData("eteris://newtab/", "http://127.0.0.1:8765/favicon.ico", ResourceType.Image, false, LocalNetwork.Decision.Allow)]
    [InlineData(null, "http://127.0.0.1:8765/", ResourceType.Image, false, LocalNetwork.Decision.Allow)]
    [InlineData("https://www.exemplo.pt/", "data:image/png;base64,AAAA", ResourceType.Image, false, LocalNetwork.Decision.Allow)]
    public void Decide_PublicPagesNeedPermissionToReachTheLocalNetwork(string? top, string request, ResourceType type, bool allowed, LocalNetwork.Decision expected)
        => Assert.Equal(expected, LocalNetwork.Decide(top, request, type, allowed));

    [Theory]
    [InlineData("https://www.exemplo.pt/artigo", "www.exemplo.pt")]
    [InlineData("http://lan-teste.example:8765/lan.html", "lan-teste.example:8765")]
    [InlineData("eteris://newtab/", null)]
    [InlineData(null, null)]
    public void SiteOf_IsThePermissionKey(string? top, string? expected) => Assert.Equal(expected, LocalNetwork.SiteOf(top));

    [Theory]
    [InlineData("http://127.0.0.1:6463/rpc", "este computador · 127.0.0.1:6463")]
    [InlineData("http://localhost:8766/echo", "este computador · localhost:8766")]
    [InlineData("http://192.168.1.1/admin", "rede local · 192.168.1.1")]
    public void Label_SaysWhereTheRequestWasGoing(string url, string expected) => Assert.Equal(expected, LocalNetwork.Label(url));

    [Fact]
    public void ShieldLine_CountsWhatWasStopped()
    {
        Assert.Contains("fora do alcance", LocalNetworkText.ShieldLine([]));
        Assert.Equal("✓  1 pedido travado para rede local · 192.168.1.1",
            LocalNetworkText.ShieldLine([new("rede local · 192.168.1.1", 1)]));
        Assert.Equal("✓  3 pedidos travados para este computador · localhost:8766 e mais 1",
            LocalNetworkText.ShieldLine([new("este computador · localhost:8766", 2), new("este computador · 127.0.0.1:8766", 1)]));
    }

    [Fact]
    public void Tab_RecordsLocalNetworkBlocksSeparatelyFromTrackers()
    {
        var tab = new BrowserTab("https://www.exemplo.pt/");
        tab.RecordLocalNetworkBlocked("rede local · 192.168.1.1");
        tab.RecordLocalNetworkBlocked("rede local · 192.168.1.1");
        Assert.Equal(0, tab.BlockedCount);
        Assert.Equal(2, Assert.Single(tab.LocalNetworkBlocked).Value);
        tab.ResetShield();
        Assert.Empty(tab.LocalNetworkBlocked);
    }

    [Fact]
    public void Grants_OnlyThisTime_AreKeptApartForPrivateWindows()
    {
        var site = "grants-" + Guid.NewGuid().ToString("N") + ".pt";
        LocalNetworkGrants.Allow(site, isPrivate: true);
        Assert.True(LocalNetworkGrants.IsAllowed(site, isPrivate: true));
        Assert.False(LocalNetworkGrants.IsAllowed(site, isPrivate: false));
        LocalNetworkGrants.ForgetPrivate();
        Assert.False(LocalNetworkGrants.IsAllowed(site, isPrivate: true));
        LocalNetworkGrants.Allow(site, isPrivate: false);
        LocalNetworkGrants.Forget(site);
        Assert.False(LocalNetworkGrants.IsAllowed(site, isPrivate: false));
    }

    [Fact]
    public void Permission_HasANameAndAPhrase()
    {
        Assert.Equal("Rede local", PermissionText.Name(SitePermission.LocalNetwork));
        Assert.Equal("falar com a sua rede local e com este computador", PermissionText.Describe([SitePermission.LocalNetwork]));
    }
}
