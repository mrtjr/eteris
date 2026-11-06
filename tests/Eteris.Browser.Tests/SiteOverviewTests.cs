using System.IO;
using Eteris.Browser.Data;
using Eteris.Browser.Permissions;

namespace Eteris.Browser.Tests;

public sealed class SiteOverviewTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-sites-" + Guid.NewGuid() + ".db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Fact]
    public void Build_MergesEverySettingPerSite()
    {
        var zoom = new Dictionary<string, double> { ["www.publico.pt"] = 1 };
        var shieldOff = new[] { "expresso.pt" };
        var permissions = new Dictionary<string, IReadOnlyList<(SitePermission, bool)>>
        {
            ["www.publico.pt"] = [(SitePermission.Location, false)],
        };
        var hidden = new Dictionary<string, int> { ["publico.pt"] = 2 };

        var sites = SiteOverview.Build(zoom, shieldOff, permissions, hidden);

        Assert.Equal(["expresso.pt", "publico.pt"], sites.Select(s => s.Site));
        var publico = sites[1];
        Assert.Equal(120, publico.ZoomPercent);
        Assert.Equal(2, publico.HiddenElements);
        Assert.Equal("Localização: bloqueado", Assert.Single(publico.Permissions));
        Assert.True(sites[0].ShieldOff);
    }

    [Fact]
    public void Stores_ListWhatTheyKeep()
    {
        var zoom = new SiteZoomStore(_path);
        zoom.Set("a.pt", 1);
        var shields = new ShieldExceptionStore(_path);
        shields.Add("www.b.pt");
        var hidden = new HiddenElementsStore(_path);
        hidden.Add("c.pt", "div.x");
        hidden.Add("c.pt", "div.y");
        var perms = new SitePermissionStore(_path);
        perms.Set("d.pt", SitePermission.Camera, true);

        Assert.Equal(1, zoom.All()["a.pt"]);
        Assert.Equal(["b.pt"], shields.All());
        Assert.Equal(2, hidden.Hosts()["c.pt"]);
        Assert.Single(perms.AllSites()["d.pt"]);
    }

    [Fact]
    public void Build_ListsStrictAndDarkSitesEvenWithoutOtherSettings()
    {
        var sites = SiteOverview.Build(new Dictionary<string, double> { ["www.publico.pt"] = 1 }, [],
            new Dictionary<string, IReadOnlyList<(Eteris.Browser.Permissions.SitePermission, bool)>>(), new Dictionary<string, int>(),
            strict: ["publico.pt"], dark: ["wikipedia.org", "publico.pt"]);
        var publico = sites.Single(s => s.Site == "publico.pt");
        Assert.Equal(["modo rigoroso", "escurecido"], publico.Flags);
        Assert.NotNull(publico.ZoomPercent);
        Assert.Equal(["escurecido"], sites.Single(s => s.Site == "wikipedia.org").Flags);
    }
}
