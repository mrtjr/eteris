using System.Collections.Specialized;
using System.IO;
using System.Net;
using CefSharp;
using Cookie = CefSharp.Cookie;
using Eteris.Browser.Permissions;
using Eteris.Browser.Shields;
using static Eteris.Browser.Tests.PrivacyRequestHandlerTests;
using CookieFilter = Eteris.Browser.Shields.PrivacyRequestHandler.ThirdPartyCookieFilter;
using PrivacyResourceHandler = Eteris.Browser.Shields.PrivacyRequestHandler.PrivacyResourceHandler;

namespace Eteris.Browser.Tests;

// Revisão aos 50: rede local e cookies de terceiros sem portas laterais (blob:, about:blank, sufixos partilhados),
// cookies «Partitioned» (CHIPS) no frasco do site de topo, intranets de nome público e endereços CGNAT/NAT64.
public sealed class ReviewLocalNetworkCookiesTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "eteris-rev50-perm-" + Guid.NewGuid() + ".db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_path);
    }

    // --- O documento de topo ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("blob:https://evil.example/6a1f", "https://evil.example/6a1f")]
    [InlineData("filesystem:https://a.pt/temporary/x", "https://a.pt/temporary/x")]
    [InlineData("https://a.pt/", "https://a.pt/")]
    [InlineData("about:blank", "about:blank")]
    [InlineData(null, null)]
    public void TopDocument_BlobIsTheOriginThatMadeIt(string? top, string? expected) => Assert.Equal(expected, TopDocument.Effective(top));

    [Theory]
    [InlineData("https://www.jornal.pt/a", "https://jornal.pt")]
    [InlineData("blob:https://alice.github.io/1", "https://alice.github.io")]
    [InlineData("http://jornal.pt/", "http://jornal.pt")]
    [InlineData("about:blank", null)]
    [InlineData("eteris://newtab/", null)]
    public void TopDocument_SiteKeyIsSchemefulETldPlusOne(string top, string? expected) => Assert.Equal(expected, TopDocument.SiteKey(top));

    // --- Cookies de terceiros -----------------------------------------------------------------------------------

    [Theory]
    // Uma página que navega para um blob: continua a ser o seu site: os píxeis de outros sites não levam cookies.
    [InlineData("blob:https://evil.example/6a1f", "https://pixel.tracker.example/p.gif", false)]
    [InlineData("blob:https://evil.example/6a1f", "https://cdn.evil.example/a.js", true)]
    // Sem origem web: os pedidos http(s) são de terceiros.
    [InlineData("about:blank", "https://pixel.tracker.example/p.gif", false)]
    [InlineData("data:text/html,<p>", "https://pixel.tracker.example/p.gif", false)]
    [InlineData("eteris://newtab/", "https://www.jornal.pt/favicon.ico", false)]
    // Vizinhos num sufixo partilhado (Public Suffix List, secção PRIVATE) são sites diferentes.
    [InlineData("https://evil.github.io/", "https://victim.github.io/p", false)]
    [InlineData("https://d1.cloudfront.net/", "https://d2.cloudfront.net/p", false)]
    [InlineData("https://www.jornal.pt/", "https://img.jornal.pt/p.png", true)]
    // Pedidos que não são da rede (data:, blob:) não têm cookies a decidir.
    [InlineData("https://www.jornal.pt/", "data:image/png;base64,AAAA", true)]
    public void ThirdPartyCookies_HaveNoSideDoors(string top, string request, bool allowed)
        => Assert.Equal(allowed, ThirdPartyCookies.Allow(top, request, ResourceType.Image, block: true));

    [Fact]
    public void Referrer_BetweenNeighboursOnASharedSuffix_IsOnlyTheOrigin()
    {
        var cap = ReferrerCap.Decide("https://alice.github.io/projeto/segredo?id=1", "https://bob.github.io/");
        Assert.Equal(ReferrerCap.Kind.Origin, cap.Kind);
        Assert.Equal("https://alice.github.io/", cap.Value);
    }

    [Theory]
    [InlineData("id=1; Secure; SameSite=None; Partitioned; Path=/", "id", true)]
    [InlineData("__Host-id=1; secure; path=/; partitioned", "__Host-id", true)]
    [InlineData("id=1; Secure; SameSite=None; Path=/", "id", false)]
    // Sem Secure o Chromium não o particiona.
    [InlineData("id=1; SameSite=None; Partitioned", "id", false)]
    [InlineData("outro=1; Secure; Partitioned", "id", false)]
    [InlineData("idx=1; Secure; Partitioned", "id", false)]
    public void IsPartitioned_ReadsTheSetCookieAttribute(string header, string name, bool expected)
        => Assert.Equal(expected, ThirdPartyCookies.IsPartitioned(["a=b; Path=/", header], name));

    [Fact]
    public void PartitionedJar_OnlyTheSavedValueUnderTheSameTopSite()
    {
        var jar = new PartitionedCookieJar();
        jar.Remember("https://jornal.pt", ".chat.example", "sid", "/", "v1");
        Assert.True(jar.Holds("https://jornal.pt", "chat.example", "sid", "/", "v1"));
        // Outro site de topo, ou um cookie normal do terceiro com o mesmo nome (outro valor), não passam.
        Assert.False(jar.Holds("https://loja.pt", "chat.example", "sid", "/", "v1"));
        Assert.False(jar.Holds("https://jornal.pt", "chat.example", "sid", "/", "id-de-rastreio"));
        Assert.Same(PartitionedCookieJar.Private, PartitionedCookieJar.For(true));
        Assert.NotSame(PartitionedCookieJar.Normal, PartitionedCookieJar.Private);
    }

    private static IBrowser Page(string url)
    {
        var (main, mainFake) = Fake<IFrame>.Create();
        mainFake.Members["get_IsValid"] = _ => true;
        mainFake.Members["get_Url"] = _ => url;
        var (browser, browserFake) = Fake<IBrowser>.Create();
        browserFake.Members["get_MainFrame"] = _ => main;
        return browser;
    }

    private static IRequest Request(string url, ResourceType type)
    {
        var (request, fake) = Fake<IRequest>.Create();
        fake.Members["get_Url"] = _ => url;
        fake.Members["get_ResourceType"] = _ => type;
        return request;
    }

    private static IResponse Response(params string[] setCookie)
    {
        var headers = new NameValueCollection();
        foreach (var line in setCookie) headers.Add("Set-Cookie", line);
        var (response, fake) = Fake<IResponse>.Create();
        fake.Members["get_Headers"] = _ => headers;
        return response;
    }

    private static Cookie Cookie(string name, string value) => new() { Name = name, Value = value, Domain = "chat.example", Path = "/", Secure = true };

    // O widget de chat embutido grava o seu cookie Partitioned e volta a recebê-lo neste site; um cookie normal do mesmo
    // terceiro continua recusado.
    [Fact]
    public void Filter_LetsPartitionedCookiesLiveInTheTopSiteJar()
    {
        var filter = new CookieFilter(_ => true, isPrivate: true);
        PartitionedCookieJar.Private.Clear();
        var page = Page("https://www.jornal.pt/artigo");
        var widget = Request("https://chat.example/frame", ResourceType.SubFrame);
        Assert.True(filter.CanSaveCookie(null!, page, null!, widget, Response("sid=v1; Secure; SameSite=None; Partitioned; Path=/"), Cookie("sid", "v1")));
        Assert.True(filter.CanSendCookie(null!, page, null!, widget, Cookie("sid", "v1")));
        Assert.False(filter.CanSaveCookie(null!, page, null!, widget, Response("uid=9; Secure; SameSite=None; Path=/"), Cookie("uid", "9")));
        Assert.False(filter.CanSendCookie(null!, page, null!, widget, Cookie("sid", "id-de-rastreio")));
        Assert.False(filter.CanSendCookie(null!, Page("https://loja.pt/"), null!, widget, Cookie("sid", "v1")));
        PartitionedCookieJar.Private.Clear();
    }

    [Fact]
    public void Filter_ExistsOnlyWithTheBlockOn_AndPrivateJarEndsWithTheSession()
    {
        var handler = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "Shields", "PrivacyRequestHandler.cs"));
        Assert.Contains("=> Data.Settings.Current.BlockThirdPartyCookies ? _cookies : null;", handler);
        var ending = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "MainWindow.Private.cs"));
        Assert.Equal(2, ending.Split("Shields.PartitionedCookieJar.Private.Clear();").Length - 1);
    }

    // --- Rede local ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("100.64.0.1", LocalNetwork.Space.Private)]
    [InlineData("100.100.100.100", LocalNetwork.Space.Private)]
    [InlineData("100.127.255.254", LocalNetwork.Space.Private)]
    [InlineData("100.63.255.255", LocalNetwork.Space.Public)]
    [InlineData("100.128.0.1", LocalNetwork.Space.Public)]
    [InlineData("198.18.0.1", LocalNetwork.Space.Private)]
    [InlineData("198.19.255.1", LocalNetwork.Space.Private)]
    [InlineData("198.20.0.1", LocalNetwork.Space.Public)]
    [InlineData("64:ff9b::c0a8:101", LocalNetwork.Space.Private)]
    [InlineData("64:ff9b::7f00:1", LocalNetwork.Space.Loopback)]
    [InlineData("64:ff9b::808:808", LocalNetwork.Space.Public)]
    public void Classify_CoversCgnatTestNetworksAndNat64(string host, LocalNetwork.Space expected)
        => Assert.Equal(expected, LocalNetwork.Classify(host));

    [Theory]
    // Uma página que se navega para um blob: continua pública.
    [InlineData("blob:https://evil.example/6a1f", "http://192.168.1.1/", LocalNetwork.Decision.Block)]
    [InlineData("blob:http://192.168.1.1/6a1f", "http://192.168.1.1/x", LocalNetwork.Decision.Allow)]
    // Sem origem web (about:blank, data:, um blob opaco) conta como público.
    [InlineData("about:blank", "http://192.168.1.1/", LocalNetwork.Decision.Block)]
    [InlineData("data:text/html,<p>", "http://127.0.0.1:6463/", LocalNetwork.Decision.Block)]
    [InlineData("blob:null/6a1f", "http://127.0.0.1:6463/", LocalNetwork.Decision.Block)]
    // As páginas do Éteris e um ficheiro local abrem o que é local.
    [InlineData("eteris://newtab/", "http://127.0.0.1:8765/favicon.ico", LocalNetwork.Decision.Allow)]
    [InlineData("file:///C:/site/index.html", "http://localhost:3000/api", LocalNetwork.Decision.Allow)]
    // Um pedido de uma página que já se foi (separador a fechar) não chega à rede local.
    [InlineData(null, "http://127.0.0.1:8765/", LocalNetwork.Decision.Block)]
    // Um endereço CGNAT (Tailscale) é rede local.
    [InlineData("https://www.exemplo.pt/", "http://100.101.102.103:8123/", LocalNetwork.Decision.Block)]
    public void Decide_HasNoSideDoors(string? top, string request, LocalNetwork.Decision expected)
        => Assert.Equal(expected, LocalNetwork.Decide(top, request, ResourceType.Xhr, allowedForSite: false));

    [Fact]
    public void SiteOf_ABlobAsksForTheSiteThatMadeIt()
    {
        Assert.Equal("evil.example", LocalNetwork.SiteOf("blob:https://evil.example/6a1f"));
        Assert.Null(LocalNetwork.SiteOf("about:blank"));
    }

    [Theory]
    [InlineData("https://intranet.empresa.pt/portal", "intranet.empresa.pt")]
    [InlineData("blob:https://ha.minhacasa.pt/x", "ha.minhacasa.pt")]
    [InlineData("http://192.168.1.1/", null)]
    [InlineData("http://router/", null)]
    [InlineData("http://nas.local/", null)]
    [InlineData("about:blank", null)]
    public void ResolvableTop_IsAPublicName(string top, string? expected) => Assert.Equal(expected, LocalNetwork.ResolvableTop(top));

    [Fact]
    public void Classify_AddressesAreLocalOnlyWhenAllAre()
    {
        Assert.Equal(LocalNetwork.Space.Private, LocalNetwork.Classify([IPAddress.Parse("10.1.2.3")]));
        Assert.Equal(LocalNetwork.Space.Private, LocalNetwork.Classify([IPAddress.Parse("127.0.0.1"), IPAddress.Parse("192.168.1.10")]));
        Assert.Equal(LocalNetwork.Space.Loopback, LocalNetwork.Classify([IPAddress.Parse("127.0.0.1"), IPAddress.Parse("::1")]));
        Assert.Equal(LocalNetwork.Space.Public, LocalNetwork.Classify([IPAddress.Parse("10.1.2.3"), IPAddress.Parse("8.8.8.8")]));
        Assert.Equal(LocalNetwork.Space.Public, LocalNetwork.Classify(Array.Empty<IPAddress>()));
    }

    [Fact]
    public async Task AddressCache_ResolvesOnceAndRemembers()
    {
        var now = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        var calls = 0;
        var cache = new AddressCache(_ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new[] { IPAddress.Parse("10.1.2.3") });
        }, () => now);
        Assert.False(cache.TryGet("intranet.empresa.pt", out _));
        Assert.Equal(LocalNetwork.Space.Private, await cache.LookupAsync("intranet.empresa.pt"));
        Assert.True(cache.TryGet("intranet.empresa.pt", out var space));
        Assert.Equal(LocalNetwork.Space.Private, space);
        Assert.Equal(1, calls);
        now += AddressCache.Ttl + TimeSpan.FromSeconds(1);
        Assert.False(cache.TryGet("intranet.empresa.pt", out _));
        // Um nome que não resolve é tratado como público (o cartão continua a perguntar).
        var failing = new AddressCache(_ => throw new System.Net.Sockets.SocketException(), () => now);
        Assert.Equal(LocalNetwork.Space.Public, await failing.LookupAsync("nao-existe.example"));
    }

    // Pelo tratador: sem origem web não há a quem perguntar (sem cartão), mas o pedido não passa.
    [Fact]
    public void Handler_AboutBlankCannotReachTheRouter_AndAsksNoOne()
    {
        var asked = new List<string>();
        var handler = new PrivacyResourceHandler(() => new NoBlock(), _ => true, _ => { }, null,
            localNetworkAllowed: _ => false, onLocalNetworkBlocked: (site, _) => asked.Add(site));
        Assert.Equal(CefReturnValue.Cancel, handler.Decide(Page("about:blank"), null, Request("http://192.168.1.1/admin", ResourceType.Image)));
        Assert.Empty(asked);
        Assert.Equal(CefReturnValue.Cancel, handler.Decide(Page("blob:https://evil.example/1"), null, Request("http://192.168.1.1/admin", ResourceType.Image)));
        Assert.Equal(["evil.example"], asked);
    }

    // Sem IRequestCallback (o caso dos testes) a página de nome público não espera pelo DNS: fica travada e pergunta.
    [Fact]
    public void Handler_PublicNameWithoutCallback_BlocksAndAsks()
    {
        var asked = new List<string>();
        var handler = new PrivacyResourceHandler(() => new NoBlock(), _ => true, _ => { }, null,
            localNetworkAllowed: _ => false, onLocalNetworkBlocked: (site, _) => asked.Add(site));
        Assert.Equal(CefReturnValue.Cancel,
            handler.Decide(Page("https://eteris-rev50-nao-existe.example/"), null, Request("http://10.1.5.20/api", ResourceType.Xhr)));
        Assert.Equal(["eteris-rev50-nao-existe.example"], asked);
    }

    // Logo depois de recarregar, o frame principal novo ainda não tem endereço na thread de rede (visto ao vivo: ""):
    // vale o endereço da navegação em curso — o cartão pergunta pelo site certo e os cookies próprios continuam a passar.
    [Fact]
    public void Handler_RightAfterReload_UsesTheNavigationAddress()
    {
        var asked = new List<string>();
        var handler = new PrivacyResourceHandler(() => new NoBlock(), _ => true, _ => { }, null,
            localNetworkAllowed: _ => false, onLocalNetworkBlocked: (site, _) => asked.Add(site));
        var reloading = Page("");
        handler.Decide(reloading, null, Request("http://192.168.1.1/painel", ResourceType.MainFrame));
        Assert.Equal("http://192.168.1.1/painel", handler.TopOf(reloading));
        handler.Decide(reloading, null, Request("http://router.example/x", ResourceType.MainFrame));
        Assert.Equal(CefReturnValue.Cancel, handler.Decide(reloading, null, Request("http://10.0.0.1/api", ResourceType.Xhr)));
        Assert.Equal(["router.example"], asked);
        // Com endereço no frame, é ele que conta.
        Assert.Equal("https://www.jornal.pt/", handler.TopOf(Page("https://www.jornal.pt/")));
    }

    private sealed class NoBlock : IRequestBlocker
    {
        public bool ShouldBlock(string requestUrl, string? pageUrl, string resourceType) => false;
    }

    // --- Permissões e janela -----------------------------------------------------------------------------------

    // A resposta fica em memória (sem SQLite por pedido travado) e uma escrita em qualquer loja da mesma base vale logo.
    [Fact]
    public void Permissions_CachedAnswersFollowEveryWrite()
    {
        var window = new SitePermissionStore(_path);
        var other = new SitePermissionStore(_path);
        Assert.Null(window.Get("nas.example", SitePermission.LocalNetwork));
        other.Set("nas.example", SitePermission.LocalNetwork, true);
        Assert.True(window.Get("nas.example", SitePermission.LocalNetwork));
        other.ForgetWhere(key => key == "nas.example");
        Assert.Null(window.Get("nas.example", SitePermission.LocalNetwork));
        // Uma base em memória (janela privada) não vê as respostas da do perfil.
        var memory = SitePermissionStore.InMemory();
        Assert.Null(memory.Get("nas.example", SitePermission.LocalNetwork));
        memory.Set("nas.example", SitePermission.LocalNetwork, true);
        Assert.True(memory.Get("nas.example", SitePermission.LocalNetwork));
        Assert.Null(window.Get("nas.example", SitePermission.LocalNetwork));
        memory.Close();
    }

    [Fact]
    public void Window_GlanceReloadAndReportsBatch()
    {
        var glance = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "MainWindow.Glance.cs"));
        Assert.Contains("localNetworkAllowed: IsLocalNetworkAllowed", glance);
        var reload = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "MainWindow.Reload.cs"));
        Assert.Contains("_localNetworkAsked.Remove(site);", reload);
        var lan = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "MainWindow.LocalNetwork.cs"));
        Assert.Contains("if (Interlocked.Exchange(ref _localNetworkDraining, 1) == 0) Dispatcher.InvokeAsync(DrainLocalNetworkReports);", lan);
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
