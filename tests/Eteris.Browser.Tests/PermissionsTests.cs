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

    // MOT-04: a câmara e o microfone decidem-se pelo par «página de topo + origem que pede»; a partilha de ecrã pergunta sempre.
    [Fact] public void Decide_Asks_WhenEmbeddedInAnotherSite()
    {
        var store = new SitePermissionStore(_path);
        store.Set("meet.example", SitePermission.Camera, true);
        Assert.Equal(PermissionDecision.Allow, PermissionRules.Decide(store, "meet.example", [SitePermission.Camera]));
        Assert.Equal(PermissionDecision.Ask, PermissionRules.Decide(store, "meet.example", [SitePermission.Camera], embedded: true));
        store.Set("meet.example", SitePermission.Microphone, false);
        Assert.Equal(PermissionDecision.Deny, PermissionRules.Decide(store, "meet.example", [SitePermission.Microphone], embedded: true));
    }

    [Fact] public void ScreenShare_IsNeverRemembered()
    {
        var store = new SitePermissionStore(_path);
        store.Set("meet.example", SitePermission.ScreenShare, true);
        Assert.Equal(PermissionDecision.Ask, PermissionRules.Decide(store, "meet.example", [SitePermission.ScreenShare]));
        store.Set("meet.example", SitePermission.ScreenShare, false);
        Assert.Equal(PermissionDecision.Ask, PermissionRules.Decide(store, "meet.example", [SitePermission.ScreenShare]));
        Assert.False(PermissionRules.CanRemember([SitePermission.ScreenShare], embedded: false));
        Assert.False(PermissionRules.CanRemember([SitePermission.Camera], embedded: true));
        Assert.True(PermissionRules.CanRemember([SitePermission.Camera, SitePermission.Microphone], embedded: false));
    }

    [Theory] [InlineData("https://meet.example/sala", "https://meet.example", null)]
    [InlineData("https://blog.example/x", "https://meet.example", "blog.example")]
    [InlineData("https://meet.example:8443/", "https://meet.example", "meet.example:8443")]
    [InlineData(null, "https://meet.example", "outra página")]
    [InlineData("", "https://meet.example", "outra página")]
    public void EmbeddedIn_NamesTheTopPage_WhenItIsAnotherSite(string? top, string origin, string? expected)
        => Assert.Equal(expected, PermissionRules.EmbeddedIn(top, origin));

    // Respond e Drop partilham uma guarda: só a primeira chamada (de qualquer dos dois) chega ao callback do motor.
    [Fact] public void RespondAndDrop_ShareOneGuard_SoTheEngineIsAnsweredOnce()
    {
        var answers = new List<string>();
        var (respond, drop) = PermissionAsk.Guard(allowed => answers.Add("responde " + allowed), () => answers.Add("larga"));
        respond(true); respond(false); drop();
        Assert.Equal(["responde True"], answers);

        answers.Clear();
        (respond, drop) = PermissionAsk.Guard(allowed => answers.Add("responde " + allowed), () => answers.Add("larga"));
        drop(); respond(true); drop();
        Assert.Equal(["larga"], answers);
    }

    [Fact] public void RespondAndDrop_SurviveAnEngineThatAlreadyLeft()
    {
        var (respond, drop) = PermissionAsk.Guard(_ => throw new ObjectDisposedException("callback"), () => throw new ObjectDisposedException("callback"));
        respond(true);
        var (_, drop2) = PermissionAsk.Guard(_ => { }, () => throw new ObjectDisposedException("callback"));
        drop2();
        drop();
    }

    [Fact] public void MediaRequests_AreDecidedByTheTopPageAndTheOriginThatAsks()
    {
        var handler = Source(Path.Combine("Permissions", "SitePermissionHandler.cs"));
        Assert.Contains("var embeddedIn = PermissionRules.EmbeddedIn(SafeFrame.MainUrl(browser), requestingOrigin);", handler);
        Assert.Contains("embedded: embeddedIn is not null", handler);
        Assert.Contains("onCapture?.Invoke(chromiumWebBrowser);", handler);
        Assert.Contains("Source: chromiumWebBrowser", handler);
        Assert.Contains("PromptId: promptId", handler);
        Assert.Contains("SitePermissionHandler(SitePermissionStore store, Action<PermissionAsk> ask, Action<CefSharp.IWebBrowser>? onCapture = null,", handler);
    }

    [Fact] public void TheCard_NamesTheTopPage_HidesRemember_AndOnlyStoresWhatCanBeRemembered()
    {
        var card = Source("MainWindow.Permissions.cs");
        Assert.Contains("\" (dentro de \" + next.EmbeddedIn + \")\"", card);
        Assert.Contains("PermissionRemember.Visibility = PermissionRememberLabel.Visibility = lan || !next.CanRemember ? Visibility.Collapsed : Visibility.Visible;", card);
        Assert.Contains("private void AskPermission(PermissionAsk ask, BrowserTab? tab = null)", card);
        Assert.Contains("private BrowserTab? TabOf(object? source)", card);
        Assert.Contains("else if (current.CanRemember && PermissionRemember.IsChecked == true)", card);
        Assert.Contains("else if (allowed && current.CanRemember)", card);
        var lan = Source("MainWindow.LocalNetwork.cs");
        // O cartão que sai sem resposta (a aba navegou ou fechou) deixa o site voltar a perguntar.
        Assert.Contains("Drop: () => _localNetworkAsked.Remove(site)", lan);
        Assert.Contains("CanRemember: false", lan);
    }

    [Fact] public void ATabUsingTheCamera_ShowsASignal()
    {
        var tab = new Eteris.Browser.Tabs.BrowserTab("https://a.pt/");
        Assert.Equal(string.Empty, tab.StatusLine);
        tab.IsCapturing = true;
        Assert.Equal("A usar a câmara, o microfone ou o ecrã", tab.StatusLine);
        tab.IsMuted = true;
        Assert.Equal("Som silenciado · A usar a câmara, o microfone ou o ecrã", tab.StatusLine);
        var xaml = Source("MainWindow.xaml");
        foreach (var name in new[] { "TabCapture", "SideCapture" })
            Assert.Contains($"x:Name=\"{name}\"", xaml);
        Assert.Equal(2, xaml.Split("<DataTrigger Binding=\"{Binding IsCapturing}\"").Length - 1);
        Assert.Matches(@"CaptureSessions\.Ended\(e\.Browser\.Identifier\);\s*Dispatcher\.InvokeAsync\(\(\) => tab\.IsCapturing = false\);", Source("MainWindow.xaml.cs"));
    }

    private static string Source(params string[] parts)
        => File.ReadAllText(Path.Combine([Sync.GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", .. parts]));
}
