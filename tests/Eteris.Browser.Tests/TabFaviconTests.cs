using System.Windows.Media;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// O ícone da aba pertence a um site: uma página de outro site sem ícone próprio (um ficheiro de vídeo aberto
// diretamente) não pode ficar com o do site anterior, mesmo quando o endereço da aba já foi escrito antes do commit
// (omnibox, troca para o motor de vídeo).
public class TabFaviconTests
{
    private static ImageSource Icon()
    {
        var image = new DrawingImage();
        image.Freeze();
        return image;
    }

    [Fact]
    public void AnotherSitesPage_DropsTheIcon_EvenWhenTheAddressWasWrittenFirst()
    {
        var tab = new BrowserTab("https://duckduckgo.com/?q=video") { FaviconSite = "https://duckduckgo.com", Favicon = Icon() };
        // A omnibox escreve o endereço novo antes de o motor o confirmar: a comparação por anfitrião já não vê mudança.
        tab.Address = "https://www.w3schools.com/html/mov_bbb.mp4";
        tab.ForgetFaviconOfOtherSite("https://www.w3schools.com/html/mov_bbb.mp4");
        Assert.Null(tab.Favicon);
    }

    [Fact]
    public void SameSitePage_KeepsTheIconUntilTheNewOneArrives()
    {
        var icon = Icon();
        var tab = new BrowserTab("https://a.pt/1") { FaviconSite = "https://a.pt", Favicon = icon };
        tab.ForgetFaviconOfOtherSite("https://a.pt/2");
        Assert.Same(icon, tab.Favicon);
    }

    [Fact]
    public void ReturningToAPage_RestoresTheIconWithItsSite()
    {
        var icon = Icon();
        var tab = new BrowserTab("https://a.pt/") { FaviconSite = "https://a.pt", Favicon = icon };
        tab.LeavePage("https://a.pt/");
        tab.Favicon = null;
        tab.FaviconSite = "https://b.pt";
        Assert.True(tab.ReturnToPage("https://a.pt/"));
        Assert.Same(icon, tab.Favicon);
        tab.ForgetFaviconOfOtherSite("https://a.pt/outra");
        Assert.Same(icon, tab.Favicon);
    }
}
