using System.IO;
using Eteris.Browser.Data;

namespace Eteris.Browser.Tests;

public sealed class SiteZoomStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-zoom-" + Guid.NewGuid() + ".db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void UnknownHost_IsDefaultZoom()
        => Assert.Equal(0, new SiteZoomStore(_path).Get("publico.pt"));

    [Fact]
    public void Set_PersistsAcrossInstances()
    {
        new SiteZoomStore(_path).Set("www.publico.pt", 1.5);
        Assert.Equal(1.5, new SiteZoomStore(_path).Get("www.publico.pt"));
    }

    [Fact]
    public void Zoom_IsPerHostIgnoringCase()
    {
        var store = new SiteZoomStore(_path);
        store.Set("GitHub.com", -1);
        Assert.Equal(-1, store.Get("github.com"));
        Assert.Equal(0, store.Get("gist.github.com"));
    }

    [Fact]
    public void SettingDefault_ForgetsHost()
    {
        var store = new SiteZoomStore(_path);
        store.Set("a.pt", 2);
        store.Set("a.pt", 0);
        Assert.Equal(0, new SiteZoomStore(_path).Get("a.pt"));
        Assert.Equal(0, store.Count);
    }
}
