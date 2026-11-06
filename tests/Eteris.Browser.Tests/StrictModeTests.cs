using System.IO;
using CefSharp;
using Eteris.Browser.Data;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

public sealed class StrictModeTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-strict-" + Guid.NewGuid() + ".db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Theory]
    [InlineData("https://cdn.example.net/app.js", ResourceType.Script, true)]
    [InlineData("https://widgets.example.net/embed", ResourceType.SubFrame, true)]
    [InlineData("https://static.publico.pt/app.js", ResourceType.Script, false)]
    [InlineData("https://cdn.example.net/photo.jpg", ResourceType.Image, false)]
    [InlineData("https://cdn.example.net/style.css", ResourceType.Stylesheet, false)]
    public void ThirdPartyScriptsAndFrames_AreBlocked(string request, ResourceType type, bool blocked)
        => Assert.Equal(blocked, StrictPolicy.ShouldBlock("https://www.publico.pt/artigo", request, type));

    [Fact]
    public void Store_KeepsSitesByRegistrableDomain()
    {
        var store = new StrictSiteStore(_path);
        store.Add("www.publico.pt");
        Assert.True(new StrictSiteStore(_path).Contains("desporto.publico.pt"));
        store.Remove("publico.pt");
        Assert.False(store.Contains("www.publico.pt"));
    }
}
