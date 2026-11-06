using System.IO;
using Eteris.Browser.Data;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

public sealed class HiddenElementsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-hidden-" + Guid.NewGuid() + ".db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void Add_PersistsPerHost()
    {
        new HiddenElementsStore(_path).Add("www.publico.pt", "div.banner");
        var store = new HiddenElementsStore(_path);
        Assert.Equal(["div.banner"], store.For("www.publico.pt"));
        Assert.Empty(store.For("expresso.pt"));
    }

    [Fact]
    public void Add_IgnoresDuplicates()
    {
        var store = new HiddenElementsStore(_path);
        store.Add("a.pt", "#x");
        store.Add("a.pt", "#x");
        Assert.Single(store.For("a.pt"));
    }

    [Fact]
    public void Clear_RemovesOnlyThatHost()
    {
        var store = new HiddenElementsStore(_path);
        store.Add("a.pt", "#x");
        store.Add("b.pt", "#y");
        store.Clear("a.pt");
        Assert.Empty(store.For("a.pt"));
        Assert.Single(store.For("b.pt"));
    }

    [Theory]
    [InlineData("div.banner", true)]
    [InlineData("#cookie-wall > div:nth-of-type(2)", true)]
    [InlineData("main > aside.promo", true)]
    [InlineData("div { color: red", false)]
    [InlineData("a } body { display:none", false)]
    [InlineData("x;y", false)]
    [InlineData("</style><script>", false)]
    [InlineData("", false)]
    public void Selector_IsValidated(string selector, bool safe)
        => Assert.Equal(safe, HiddenElements.IsSafeSelector(selector));

    [Fact]
    public void Css_HidesEachSelectorSeparately()
    {
        var css = HiddenElements.Css(["div.a", "#b"]);
        Assert.Contains("div.a { display: none !important; }", css);
        Assert.Contains("#b { display: none !important; }", css);
    }

    [Fact]
    public void Css_SkipsUnsafeSelectors()
        => Assert.DoesNotContain("body", HiddenElements.Css(["a } body {"]));
}
