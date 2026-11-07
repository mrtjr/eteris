using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

public class PageMenuTests
{
    private static PageContext Ctx(string? link = null, string? image = null, string selection = "", bool editable = false,
        bool back = true, bool forward = false, bool isPrivate = false)
        => new(link, image, selection, editable, back, forward, isPrivate);

    private static List<PageCommand?> Commands(PageContext ctx)
        => PageMenu.Build(ctx, "DuckDuckGo").Select(i => i?.Command).ToList();

    [Fact]
    public void PlainPage_HasNavigationAndTools()
    {
        var cmds = Commands(Ctx());
        Assert.Equal(PageCommand.Back, cmds[0]);
        Assert.Contains(PageCommand.Reload, cmds);
        Assert.Contains(PageCommand.ViewSource, cmds);
        Assert.Equal(PageCommand.Inspect, cmds[^1]);
    }

    [Fact]
    public void HideElement_OnlyOnWebPages()
    {
        Assert.Contains(PageCommand.HideElement, Commands(Ctx()));
        var internalPage = PageMenu.Build(new PageContext(null, null, "", false, false, false, false, IsWebPage: false), "DuckDuckGo");
        Assert.DoesNotContain(internalPage, i => i?.Command == PageCommand.HideElement);
    }

    [Fact]
    public void ForwardDisabledWhenNoHistory()
    {
        var forward = PageMenu.Build(Ctx(forward: false), "DuckDuckGo").First(i => i?.Command == PageCommand.Forward)!;
        Assert.False(forward.Enabled);
    }

    [Fact]
    public void Link_OffersOpenAndCopyButNotNavigation()
    {
        var cmds = Commands(Ctx(link: "https://a.pt/"));
        Assert.Equal(PageCommand.OpenLinkNewTab, cmds[0]);
        Assert.Contains(PageCommand.OpenLinkPrivate, cmds);
        Assert.Contains(PageCommand.CopyLink, cmds);
        Assert.DoesNotContain(PageCommand.Back, cmds);
    }

    [Fact]
    public void Link_OffersPreviewOnlyForWebLinks()
    {
        Assert.Contains(PageCommand.PreviewLink, Commands(Ctx(link: "https://a.pt/artigo")));
        Assert.Contains(PageCommand.PreviewLink, Commands(Ctx(link: "https://a.pt/artigo", isPrivate: true)));
        Assert.DoesNotContain(PageCommand.PreviewLink, Commands(Ctx(link: "mailto:a@a.pt")));
        Assert.Equal("Alt+clique", PageMenu.Build(Ctx(link: "https://a.pt/artigo"), "DuckDuckGo").First(i => i?.Command == PageCommand.PreviewLink)!.Gesture);
        Assert.DoesNotContain(PageCommand.PreviewLink, Commands(Ctx(link: "javascript:void(0)")));
    }

    [Fact]
    public void PrivateWindow_DoesNotOfferPrivateWindowAgain()
        => Assert.DoesNotContain(PageCommand.OpenLinkPrivate, Commands(Ctx(link: "https://a.pt/", isPrivate: true)));

    [Fact]
    public void Image_OffersSaveAndCopyAddress()
    {
        var cmds = Commands(Ctx(image: "https://a.pt/x.png"));
        Assert.Contains(PageCommand.OpenImageNewTab, cmds);
        Assert.Contains(PageCommand.SaveImage, cmds);
        Assert.Contains(PageCommand.CopyImageUrl, cmds);
    }

    [Fact]
    public void Selection_OffersCopyAndSearchWithShortQuote()
    {
        var items = PageMenu.Build(Ctx(selection: "uma frase muito comprida que não cabe no menu"), "DuckDuckGo");
        var search = items.First(i => i?.Command == PageCommand.SearchSelection)!;
        Assert.Equal("Pesquisar «uma frase muito compri…» com DuckDuckGo", search.Label);
        Assert.Contains(items, i => i?.Command == PageCommand.Copy);
    }

    [Fact]
    public void Editable_OffersClipboardWithCutDisabledWithoutSelection()
    {
        var items = PageMenu.Build(Ctx(editable: true), "DuckDuckGo");
        Assert.False(items.First(i => i?.Command == PageCommand.Cut)!.Enabled);
        Assert.True(items.First(i => i?.Command == PageCommand.Paste)!.Enabled);
        Assert.Contains(items, i => i?.Command == PageCommand.SelectAll);
    }

    [Fact]
    public void Separators_NeverLeadTrailOrRepeat()
    {
        foreach (var ctx in new[] { Ctx(), Ctx(link: "https://a.pt/", image: "https://a.pt/i.png", selection: "x"), Ctx(editable: true, selection: "y") })
        {
            var items = PageMenu.Build(ctx, "DuckDuckGo");
            Assert.NotNull(items[0]);
            Assert.NotNull(items[^1]);
            for (var i = 1; i < items.Count; i++) Assert.False(items[i] is null && items[i - 1] is null);
        }
    }
}
