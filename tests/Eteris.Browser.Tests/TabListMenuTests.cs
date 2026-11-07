using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

public class TabListMenuTests
{
    private static BrowserTab Tab(string address, string title) => new(address) { Title = title };

    [Fact]
    public void Rows_KeepStripOrder_AndFilterWithoutAccents()
    {
        var tabs = new[] { Tab("https://b.pt/", "Café"), Tab("https://a.pt/", "Abelha"), Tab("https://c.pt/", "Cafetaria") };
        Assert.Equal(["Café", "Abelha", "Cafetaria"], TabListMenu.Rows(tabs, null, "").Select(r => r.Title));
        Assert.Equal(["Café", "Cafetaria"], TabListMenu.Rows(tabs, null, "cafe").Select(r => r.Title));
    }

    [Fact]
    public void InitialIndex_IsTheActiveTab_WithoutQuery()
    {
        var tabs = new[] { Tab("https://a.pt/", "A"), Tab("https://b.pt/", "B"), Tab("https://c.pt/", "C") };
        var rows = TabListMenu.Rows(tabs, tabs[2], "");
        Assert.Equal(2, TabListMenu.InitialIndex(rows, tabs[2], ""));
    }

    [Fact]
    public void InitialIndex_IsTheFirstResult_WithQuery()
    {
        var tabs = new[] { Tab("https://a.pt/", "A"), Tab("https://b.pt/", "B"), Tab("https://c.pt/", "Bb") };
        var rows = TabListMenu.Rows(tabs, tabs[2], "b");
        Assert.Equal(0, TabListMenu.InitialIndex(rows, tabs[2], "b"));
        Assert.Equal(-1, TabListMenu.InitialIndex([], tabs[2], "zzz"));
    }

    [Theory]
    [InlineData(1, 1, "", "1 aba aberta")]
    [InlineData(15, 15, "", "15 abas abertas · pela ordem da faixa")]
    [InlineData(3, 15, "wiki", "3 de 15 abas")]
    [InlineData(0, 15, "zzz", "Nenhuma aba corresponde")]
    public void Status_TellsWhatIsShown(int shown, int total, string query, string expected)
        => Assert.Equal(expected, TabListMenu.Status(shown, total, query));

    [Theory]
    [InlineData(19, false, false)]
    [InlineData(20, false, true)]
    [InlineData(40, true, false)]
    public void SuggestsVerticalTabs_From20Tabs_UnlessAlreadyVertical(int count, bool vertical, bool expected)
        => Assert.Equal(expected, TabListMenu.SuggestsVerticalTabs(count, vertical));

    [Fact]
    public void Move_WrapsAround()
    {
        Assert.Equal(1, TabListMenu.Move(0, 1, 3));
        Assert.Equal(2, TabListMenu.Move(0, -1, 3));
        Assert.Equal(0, TabListMenu.Move(2, 1, 3));
        Assert.Equal(-1, TabListMenu.Move(0, 1, 0));
    }
}
