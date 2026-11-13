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

    // A cache de páginas repõe o documento tal como estava: para saber se correu sem a proteção que o site tem agora
    // (Shields/ShieldTightening.cs) conta a hora em que ele carregou, não a hora em que foi deixado. Um redirecionamento
    // repete a mesma hora; um documento novo no mesmo endereço (o recarregar da aba quando a proteção muda) substitui-a.
    [Fact]
    public void LeavingAPage_RemembersWhenItsDocumentLoaded()
    {
        var tab = new BrowserTab("https://a.pt/");
        Assert.Null(tab.LoadedAt("https://a.pt/"));
        tab.RecordBlocked("ads.net");
        tab.LeavePage("https://a.pt/", loadedAt: 100);
        Assert.Equal(100, tab.LoadedAt("https://a.pt/"));
        tab.LeavePage("https://a.pt/", loadedAt: 100);
        Assert.Equal(100, tab.LoadedAt("https://a.pt/"));
        tab.LeavePage("https://a.pt/", loadedAt: 500);
        Assert.Equal(500, tab.LoadedAt("https://a.pt/"));
        tab.RecordBlocked("ads.net");
        tab.LeavePage("https://a.pt/", loadedAt: 900);
        Assert.Equal(900, tab.LoadedAt("https://a.pt/"));
        // Sem a hora de carga, conta como carregada antes de tudo: pelo seguro.
        tab.LeavePage("https://b.pt/");
        Assert.Equal(long.MinValue, tab.LoadedAt("https://b.pt/"));
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
