using System.IO;
using Eteris.Browser.Data;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

public sealed class DarkPagesTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-dark-" + Guid.NewGuid() + ".db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Theory]
    [InlineData("https://www.publico.pt/x", true)]
    [InlineData("http://127.0.0.1:8765/", true)]
    [InlineData("eteris://settings/", false)]
    [InlineData("file:///C:/a.html", false)]
    [InlineData("não é url", false)]
    public void IsCandidate_OnlyWebPages(string url, bool expected) => Assert.Equal(expected, DarkPages.IsCandidate(url));

    [Fact]
    public void Scripts_AddAndRemoveTheSameStyleOnce()
    {
        Assert.Contains("invert(.9) hue-rotate(180deg)", DarkPages.EarlyScript);
        Assert.Contains("getElementById('eteris-dark')) return", DarkPages.Live(true));
        Assert.Contains("style.remove()", DarkPages.Live(false));
        Assert.DoesNotContain("innerHTML", DarkPages.Live(true));
    }

    [Fact]
    public void DarkSites_AreRememberedPerSiteAndSeparateFromStrictSites()
    {
        var dark = new StrictSiteStore(_path, "dark_sites");
        var strict = new StrictSiteStore(_path);
        dark.Add("www.publico.pt");
        Assert.True(new StrictSiteStore(_path, "dark_sites").Contains("publico.pt"));
        Assert.True(dark.Contains("desporto.publico.pt"));
        Assert.False(strict.Contains("publico.pt"));
        dark.Remove("publico.pt");
        Assert.False(new StrictSiteStore(_path, "dark_sites").Contains("www.publico.pt"));
    }
}
