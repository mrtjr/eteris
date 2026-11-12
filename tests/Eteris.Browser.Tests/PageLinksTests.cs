using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

public class PageLinksTests
{
    [Theory]
    [InlineData("https://a.pt/x", "https://b.pt/", true)]
    [InlineData("http://a.pt/x", null, true)]
    [InlineData("HTTPS://A.PT/x", "eteris://history/", true)]
    [InlineData("eteris://settings/", "https://b.pt/", false)]
    [InlineData("eteris://settings/", "about:blank", false)] // pop-up em branco escrito por um site
    [InlineData("eteris://settings/", null, false)]
    [InlineData("eteris://settings/", "eteris://autofill/", true)] // links entre páginas internas
    [InlineData("file:///C:/b.html", "file:///C:/a.html", true)] // documentação local
    [InlineData("file:///C:/x.html", "https://b.pt/", false)]
    [InlineData("file://srv/x", "eteris://newtab/", false)]
    [InlineData("data:text/html,<h1>x</h1>", "https://b.pt/", false)]
    [InlineData("javascript:alert(1)", "eteris://newtab/", false)]
    [InlineData("view-source:https://a.pt/", "https://b.pt/", false)]
    [InlineData("blob:https://a.pt/1", "https://a.pt/", false)]
    [InlineData("about:blank#blocked", "https://b.pt/", false)]
    [InlineData(null, "https://b.pt/", false)]
    public void Links_OnlyWebAddressesFromAWebPage(string? url, string? page, bool expected)
        => Assert.Equal(expected, PageLinks.CanOpenLink(url, page));

    [Theory]
    [InlineData("https://a.pt/i.png", true)]
    [InlineData("blob:https://a.pt/1", true)]
    [InlineData("data:image/png;base64,AAAA", true)]
    [InlineData("data:image/svg+xml,<svg onload=alert(1)>", false)]
    [InlineData("data:text/html,x", false)]
    [InlineData("file:///C:/i.png", false)]
    public void Images_WebBlobAndRasterData(string url, bool expected) => Assert.Equal(expected, PageLinks.CanOpenImage(url));

    [Theory]
    [InlineData("https://b.pt/", "https://a.pt/x", true)]
    [InlineData("about:blank", "https://a.pt/x", true)]
    [InlineData("", "https://a.pt/x", true)]
    [InlineData("blob:https://a.pt/uuid", "https://a.pt/x", true)] // PDF gerado pela própria página
    [InlineData("blob:https://b.pt/uuid", "https://a.pt/x", false)]
    [InlineData("data:text/html,<form>", "https://a.pt/x", false)]
    [InlineData("javascript:alert(1)", "https://a.pt/x", false)]
    [InlineData("file:///C:/x", "https://a.pt/x", false)]
    [InlineData("eteris://settings/", "https://a.pt/x", false)]
    [InlineData("eteris://settings/#syncTopicItem", "eteris://autofill/", true)] // Ctrl+clique num link entre páginas internas
    [InlineData("file:///C:/b.html", "file:///C:/a.html", true)]
    [InlineData("https://b.pt/", null, true)]
    public void Popups_NonWebTargets_AreRefused(string target, string? opener, bool expected)
        => Assert.Equal(expected, PageLinks.CanOpenPopup(target, opener));

    [Fact]
    public void DropTarget_RendererTaintFormat_IsTheChromiumOne()
        => Assert.Equal("chromium/x-renderer-taint", Eteris.Browser.Omnibox.DropTarget.RendererTaintFormat);
}
