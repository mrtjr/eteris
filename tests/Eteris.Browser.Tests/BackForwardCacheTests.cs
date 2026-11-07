using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// Voltar/Avançar pela cache de páginas (docs/motor.md, F3): a página volta sem pedidos novos, e com ela a contagem do
// Escudo que tinha.
public class BackForwardCacheTests
{
    [Fact]
    public void ReturningToAPage_RestoresItsShieldCount()
    {
        var tab = new BrowserTab("https://a.pt/");
        tab.RecordBlocked("ads.net");
        tab.RecordBlocked("ads.net");
        tab.RecordBlocked("track.io");
        tab.LeavePage("https://a.pt/");
        Assert.Equal(0, tab.BlockedCount);
        tab.RecordBlocked("outro.net");
        tab.LeavePage("https://b.pt/");
        Assert.True(tab.ReturnToPage("https://a.pt/"));
        Assert.Equal(3, tab.BlockedCount);
        Assert.Equal([new("ads.net", 2), new("track.io", 1)], tab.BlockedHosts);
    }

    [Fact]
    public void ARedirect_DoesNotOverwriteTheSavedCount()
    {
        var tab = new BrowserTab("https://a.pt/");
        tab.RecordBlocked("ads.net");
        tab.LeavePage("https://a.pt/");
        tab.LeavePage("https://a.pt/"); // 2.º pedido da mesma navegação (redirecionamento): a contagem já recomeçou
        Assert.True(tab.ReturnToPage("https://a.pt/"));
        Assert.Equal(1, tab.BlockedCount);
    }

    [Fact]
    public void UnknownPage_StartsFromZero()
    {
        var tab = new BrowserTab("https://a.pt/");
        tab.RecordBlocked("ads.net");
        Assert.False(tab.ReturnToPage("https://nunca.pt/"));
        tab.LeavePage(null);
        Assert.Equal(0, tab.BlockedCount);
    }

    [Fact]
    public void OnlyTheLastPagesAreRemembered()
    {
        var tab = new BrowserTab("https://a.pt/");
        for (var i = 0; i <= BrowserTab.RememberedPages; i++)
        {
            tab.RecordBlocked("ads.net");
            tab.LeavePage($"https://site{i}.pt/");
        }
        Assert.False(tab.ReturnToPage("https://site0.pt/"));
        Assert.True(tab.ReturnToPage($"https://site{BrowserTab.RememberedPages}.pt/"));
    }
}
