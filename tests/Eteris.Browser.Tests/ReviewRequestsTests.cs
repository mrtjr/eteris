using CefSharp;
using Eteris.Browser.Shields;
using static Eteris.Browser.Tests.PrivacyRequestHandlerTests;
using PrivacyResourceHandler = Eteris.Browser.Shields.PrivacyRequestHandler.PrivacyResourceHandler;

namespace Eteris.Browser.Tests;

// Revisão aos 50: o Observatório só muda de página quando a página nova entra em vigor (commit). Um pedido da página
// principal que não chega a ser página (transferência, 204, Esc) não o reinicia nem lhe muda o site.
public class ReviewRequestsTests
{
    private static SiteObservatory Jornal()
    {
        var o = new SiteObservatory();
        o.Reset("https://www.jornal.pt/artigo");
        o.Record("https://ads.tracker.example/p.gif", ResourceType.Image, ObservedVerdict.Blocked);
        return o;
    }

    [Fact]
    public void Download_KeepsThePageAndItsSite()
    {
        var o = Jornal();
        o.Navigating();
        // O ficheiro descarregou-se: a página à vista continua a ser o jornal.
        o.Sync("https://www.jornal.pt/artigo");
        o.Record("https://www.jornal.pt/img.png", ResourceType.Image, ObservedVerdict.Allowed);
        var snap = o.Snapshot();
        Assert.Equal("jornal.pt", snap.Site);
        Assert.Single(snap.ThirdParties);
    }

    [Fact]
    public void NewDocument_StartsAtTheFirstRequestOfThePage_AndTheCommitDoesNotEraseIt()
    {
        var o = Jornal();
        o.Navigating();
        o.Sync("https://outro.pt/");
        o.Record("https://cdn.anuncios.example/a.js", ResourceType.Script, ObservedVerdict.Allowed);
        o.Commit("https://outro.pt/");
        var snap = o.Snapshot();
        Assert.Equal("outro.pt", snap.Site);
        Assert.Equal("cdn.anuncios.example", Assert.Single(snap.ThirdParties).Host);
    }

    [Fact]
    public void ReloadOfTheSameAddress_StartsAtTheCommit()
    {
        var o = Jornal();
        o.Navigating();
        o.Sync("https://www.jornal.pt/artigo");
        o.Commit("https://www.jornal.pt/artigo");
        Assert.Empty(o.Snapshot().ThirdParties);
    }

    // pushState/#âncora mudam o endereço sem navegação (sem FrameLoadStart): nada recomeça.
    [Fact]
    public void AddressChangeWithoutNavigation_KeepsEverything()
    {
        var o = Jornal();
        o.Sync("https://www.jornal.pt/outro-artigo");
        Assert.Single(o.Snapshot().ThirdParties);
    }

    // Um documento novo que entra em vigor sem pedido nosso (cache de Voltar/Avançar) também recomeça.
    [Fact]
    public void CommitOfAnotherDocument_StartsANewPage()
    {
        var o = Jornal();
        o.Commit("https://outro.pt/");
        Assert.Equal("outro.pt", o.Snapshot().Site);
        Assert.Empty(o.Snapshot().ThirdParties);
    }

    // Pelo tratador de pedidos: o pedido da transferência (outro site) não troca o site da página à vista.
    [Fact]
    public void Handler_DownloadRequestDoesNotResetTheObservatory()
    {
        var o = Jornal();
        var handler = new PrivacyResourceHandler(() => new NoBlock(), _ => true, _ => { }, null, observatory: o);
        var (main, mainFake) = Fake<IFrame>.Create();
        mainFake.Members["get_IsValid"] = _ => true;
        mainFake.Members["get_Url"] = _ => "https://www.jornal.pt/artigo";
        var (browser, browserFake) = Fake<IBrowser>.Create();
        browserFake.Members["get_MainFrame"] = _ => main;
        handler.Decide(browser, main, Request("https://cdn.outro.com/ficheiro.zip", ResourceType.MainFrame));
        handler.Decide(browser, main, Request("https://www.jornal.pt/img.png", ResourceType.Image));
        var snap = o.Snapshot();
        Assert.Equal("jornal.pt", snap.Site);
        Assert.Contains(snap.ThirdParties, d => d.Host == "ads.tracker.example");
        Assert.DoesNotContain(snap.ThirdParties, d => d.Host == "www.jornal.pt");
    }

    private sealed class NoBlock : IRequestBlocker
    {
        public bool ShouldBlock(string requestUrl, string? pageUrl, string resourceType) => false;
    }

    private static IRequest Request(string url, ResourceType type)
    {
        var (request, fake) = Fake<IRequest>.Create();
        fake.Members["get_Url"] = _ => url;
        fake.Members["get_ResourceType"] = _ => type;
        return request;
    }
}
