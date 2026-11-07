using System.Windows.Media;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

public class TabCardTests
{
    [Theory]
    [InlineData("https://www.publico.pt/artigo", "publico.pt")]
    [InlineData("eteris://history/", "Éteris")]
    [InlineData("eteris://newtab/", "Éteris")]
    [InlineData("http://localhost:8765/x", "localhost:8765")]
    public void Host_IsFriendly(string address, string host)
        => Assert.Equal(host, new BrowserTab(address).Host);

    [Fact]
    public void StatusLine_CombinesStates()
    {
        var tab = new BrowserTab("https://a.pt/") { IsSleeping = true, IsPinned = true };
        tab.RecordBlocked("t.com");
        tab.RecordBlocked("t.com");
        Assert.Equal("Fixada · A dormir (memória libertada) · 2 rastreadores bloqueados", tab.StatusLine);
    }

    [Fact]
    public void StatusLine_EmptyWhenNothingToSay()
        => Assert.Equal(string.Empty, new BrowserTab("https://a.pt/").StatusLine);

    [Fact]
    public void StatusLine_RaisesChangeWhenStateChanges()
    {
        var tab = new BrowserTab("https://a.pt/");
        var raised = new List<string?>();
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        tab.IsAudible = true;
        Assert.Contains(nameof(BrowserTab.StatusLine), raised);
        Assert.Equal("A tocar som", tab.StatusLine);
    }

    [Fact]
    public void Thumbnail_SurvivesNavigationWithinSameHost()
    {
        var tab = new BrowserTab("https://a.pt/x") { Thumbnail = new DrawingImage() };
        var before = tab.ThumbnailVersion;
        tab.Address = "https://a.pt/y#z";
        Assert.NotNull(tab.Thumbnail);
        Assert.Equal(before + 1, tab.ThumbnailVersion);
    }

    [Fact]
    public void Thumbnail_ClearedWhenHostChanges()
    {
        var tab = new BrowserTab("https://a.pt/x") { Thumbnail = new DrawingImage() };
        var before = tab.ThumbnailVersion;
        tab.Address = "https://b.pt/x";
        Assert.Null(tab.Thumbnail);
        Assert.Equal(before + 1, tab.ThumbnailVersion);
    }

    [Fact]
    public void Thumbnail_ClearedWhenLeavingInternalPage()
    {
        var tab = new BrowserTab("eteris://newtab/") { Thumbnail = new DrawingImage() };
        tab.Address = "https://a.pt/";
        Assert.Null(tab.Thumbnail);
    }

    [Theory]
    [InlineData("https://a.pt/x", "https://A.PT/y", true)]
    [InlineData("https://a.pt/x", "https://b.pt/x", false)]
    [InlineData("https://a.pt/x", "https://www.a.pt/x", false)]
    [InlineData("eteris://newtab/", "eteris://newtab/?x", true)]
    [InlineData("eteris://newtab/", "eteris://history/", false)]
    [InlineData("not a url", "https://a.pt/", false)]
    public void SameHost_ComparesHostsIgnoringCase(string a, string b, bool expected)
        => Assert.Equal(expected, BrowserTab.SameHost(a, b));
}
