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
}
