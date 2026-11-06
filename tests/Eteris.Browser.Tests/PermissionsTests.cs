using System.IO;
using Eteris.Browser.Permissions;

namespace Eteris.Browser.Tests;

public sealed class PermissionsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-perm-" + Guid.NewGuid() + ".db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    [Theory]
    [InlineData(new[] { SitePermission.Camera }, "usar a câmara")]
    [InlineData(new[] { SitePermission.Camera, SitePermission.Microphone }, "usar a câmara e o microfone")]
    [InlineData(new[] { SitePermission.Location }, "saber a sua localização")]
    [InlineData(new[] { SitePermission.Notifications, SitePermission.Location, SitePermission.Microphone },
        "usar o microfone, saber a sua localização e mostrar notificações")]
    public void Describe_IsNaturalPortuguese(SitePermission[] kinds, string expected)
        => Assert.Equal(expected, PermissionText.Describe(kinds));

    [Fact]
    public void Store_RemembersPerSiteAndKind()
    {
        new SitePermissionStore(_path).Set("meet.google.com", SitePermission.Camera, allowed: true);
        var store = new SitePermissionStore(_path);
        Assert.True(store.Get("meet.google.com", SitePermission.Camera));
        Assert.Null(store.Get("meet.google.com", SitePermission.Microphone));
        Assert.Null(store.Get("zoom.us", SitePermission.Camera));
    }

    [Fact]
    public void Store_ForgetSite_RemovesAllDecisions()
    {
        var store = new SitePermissionStore(_path);
        store.Set("a.pt", SitePermission.Camera, true);
        store.Set("a.pt", SitePermission.Location, false);
        Assert.Equal(2, store.ForSite("a.pt").Count);
        store.Forget("a.pt");
        Assert.Empty(store.ForSite("a.pt"));
    }

    [Fact]
    public void Decide_AllRememberedAllowed_Allows()
    {
        var store = new SitePermissionStore(_path);
        store.Set("a.pt", SitePermission.Camera, true);
        store.Set("a.pt", SitePermission.Microphone, true);
        Assert.Equal(PermissionDecision.Allow, PermissionRules.Decide(store, "a.pt", [SitePermission.Camera, SitePermission.Microphone]));
    }

    [Fact]
    public void Decide_AnyRememberedDenied_Denies()
    {
        var store = new SitePermissionStore(_path);
        store.Set("a.pt", SitePermission.Camera, true);
        store.Set("a.pt", SitePermission.Microphone, false);
        Assert.Equal(PermissionDecision.Deny, PermissionRules.Decide(store, "a.pt", [SitePermission.Camera, SitePermission.Microphone]));
    }

    [Fact]
    public void Decide_SomethingUnknown_Asks()
    {
        var store = new SitePermissionStore(_path);
        store.Set("a.pt", SitePermission.Camera, true);
        Assert.Equal(PermissionDecision.Ask, PermissionRules.Decide(store, "a.pt", [SitePermission.Camera, SitePermission.Microphone]));
    }

    [Fact]
    public void Decide_NothingRecognised_Denies()
        => Assert.Equal(PermissionDecision.Deny, PermissionRules.Decide(new SitePermissionStore(_path), "a.pt", []));

    [Theory]
    [InlineData("https://meet.google.com", "meet.google.com")]
    [InlineData("https://meet.google.com/abc-defg", "meet.google.com")]
    [InlineData("http://localhost:3000", "localhost:3000")]
    public void Site_IsHostWithNonDefaultPort(string origin, string expected)
        => Assert.Equal(expected, PermissionRules.Site(origin));

    [Fact]
    public void NotificationOnlyPrompts_AreAutoDeniedByDefault()
    {
        Assert.True(PermissionRules.AutoDeny([SitePermission.Notifications], blockNotificationPrompts: true));
        Assert.False(PermissionRules.AutoDeny([SitePermission.Notifications], blockNotificationPrompts: false));
        Assert.False(PermissionRules.AutoDeny([SitePermission.Notifications, SitePermission.Camera], blockNotificationPrompts: true));
        Assert.False(PermissionRules.AutoDeny([SitePermission.Location], blockNotificationPrompts: true));
        Assert.True(new Eteris.Browser.Data.UserSettings().BlockNotificationPrompts);
    }
}
