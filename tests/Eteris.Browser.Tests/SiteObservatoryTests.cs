using CefSharp;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

// B9 — Observatório do site: o registo por pedido (barato, seguro entre threads), a agregação por domínio e por
// empresa e o mapa domínio → empresa embutido.
public class SiteObservatoryTests
{
    [Theory]
    [InlineData("https://www.google-analytics.com/g/collect?v=2", "www.google-analytics.com")]
    [InlineData("http://user:pw@cdn.exemplo.pt:8080/a.js", "cdn.exemplo.pt")]
    [InlineData("wss://socket.exemplo.pt/ws", "socket.exemplo.pt")]
    [InlineData("https://[::1]:8443/x", "[::1]")]
    [InlineData("https://exemplo.pt", "exemplo.pt")]
    [InlineData("https://exemplo.pt?q=1", "exemplo.pt")]
    [InlineData("data:image/png;base64,AAAA", "")]
    [InlineData("blob:https://exemplo.pt/123", "")]
    [InlineData("eteris://newtab/", "")]
    [InlineData("", "")]
    public void HostOf_ReadsTheHostWithoutPortOrCredentials(string url, string expected)
        => Assert.Equal(expected, SiteObservatory.HostOf(url).ToString());

    private static SiteObservatory Sample()
    {
        var o = new SiteObservatory();
        o.Reset("https://www.jornal.pt/artigo");
        o.Record("https://static.jornal.pt/app.js", ResourceType.Script, ObservedVerdict.Allowed);
        o.Record("https://www.jornal.pt/api", ResourceType.Xhr, ObservedVerdict.Allowed);
        o.Record("https://www.googletagmanager.com/gtm.js", ResourceType.Script, ObservedVerdict.Blocked);
        o.Record("https://www.google-analytics.com/g/collect", ResourceType.Ping, ObservedVerdict.Blocked);
        o.Record("https://www.google-analytics.com/g/collect", ResourceType.Ping, ObservedVerdict.Blocked);
        o.Record("https://fonts.gstatic.com/s/a.woff2", ResourceType.FontResource, ObservedVerdict.Allowed);
        o.Record("https://connect.facebook.net/pt_PT/fbevents.js", ResourceType.Script, ObservedVerdict.Blocked);
        o.Record("https://cdn.desconhecido.example/x.png", ResourceType.Image, ObservedVerdict.Allowed);
        o.Record("https://cdn.desconhecido.example/y.png", ResourceType.Image, ObservedVerdict.Allowed);
        return o;
    }

    [Fact]
    public void Snapshot_SeparatesTheSiteFromThirdParties_InOrderOfAppearance()
    {
        var snap = Sample().Snapshot();
        Assert.Equal("jornal.pt", snap.Site);
        Assert.Equal(2, snap.FirstPartyRequests);
        Assert.Equal(["www.googletagmanager.com", "www.google-analytics.com", "fonts.gstatic.com", "connect.facebook.net", "cdn.desconhecido.example"],
            snap.ThirdParties.Select(d => d.Host));
        var ga = snap.ThirdParties[1];
        Assert.Equal(2, ga.Requests);
        Assert.Equal(2, ga.Blocked);
        Assert.Equal(NodeColor.Blocked, ga.Color);
        Assert.Equal([(ObservedKind.Beacon, 2)], ga.Kinds);
        Assert.Equal(NodeColor.Allowed, snap.ThirdParties[2].Color);
        Assert.Equal("2 imagens", SiteObservatory.KindsText(snap.ThirdParties[4]));
    }

    [Fact]
    public void ByCompany_GroupsKnownOwnersAndLeavesTheRestInOthers()
    {
        var groups = SiteObservatory.ByCompany(Sample().Snapshot().ThirdParties);
        Assert.Equal(["Google", "Meta", "Outros"], groups.Select(g => g.Name));
        Assert.Equal(4, groups[0].Requests);
        Assert.Equal(3, groups[0].Blocked);
        Assert.Equal("www.google-analytics.com", groups[0].Domains[0].Host);
        Assert.False(groups[2].Known);
    }

    [Fact]
    public void Summary_NamesTheCompaniesTheShieldShows()
    {
        Assert.Equal("Esta página contactou 5 domínios de terceiros: Google (3), Meta (1)",
            SiteObservatory.Summary(SiteObservatory.ByCompany(Sample().Snapshot().ThirdParties)));
        Assert.Equal("Esta página não contactou outros sites", SiteObservatory.Summary([]));
        var o = new SiteObservatory();
        o.Reset("https://www.site.pt/");
        o.Record("https://cdn.desconhecido.example/x.png", ResourceType.Image, ObservedVerdict.Allowed);
        Assert.Equal("Esta página contactou 1 domínio de terceiros", SiteObservatory.Summary(SiteObservatory.ByCompany(o.Snapshot().ThirdParties)));
    }

    [Fact]
    public void Verdict_TheStrongestDecisionStays_AndTheUserColourWins()
    {
        var o = new SiteObservatory();
        o.Reset("https://www.site.pt/");
        o.Record("https://t.tracker.example/a", ResourceType.Script, ObservedVerdict.Blocked);
        o.Record("https://t.tracker.example/b", ResourceType.Image, ObservedVerdict.Allowed);
        o.Record("https://alias.site.example/c", ResourceType.Script, ObservedVerdict.Cloaked, "x.eulerian.net");
        o.Record("https://meu.example/d", ResourceType.Script, ObservedVerdict.UserBlocked);
        var d = o.Snapshot().ThirdParties;
        Assert.Equal(ObservedVerdict.Blocked, d[0].Verdict);
        Assert.Equal(1, d[0].Allowed);
        Assert.Equal("1 de 2 pedidos bloqueados pelas listas do Escudo", SiteObservatory.VerdictText(d[0]));
        Assert.Equal("Rastreador escondido (CNAME → x.eulerian.net), bloqueado", SiteObservatory.VerdictText(d[1]));
        Assert.Equal(NodeColor.User, d[2].Color);
    }

    [Fact]
    public void ShieldOff_IsAnAmberException_NotABlock()
    {
        var o = new SiteObservatory();
        o.Reset("https://www.site.pt/");
        o.Record("https://www.google-analytics.com/a", ResourceType.Script, ObservedVerdict.ShieldOff);
        var d = Assert.Single(o.Snapshot().ThirdParties);
        Assert.Equal(0, d.Blocked);
        Assert.Equal(NodeColor.User, d.Color);
        Assert.Equal("Permitido: desligou o Escudo neste site", SiteObservatory.VerdictText(d));
    }

    [Fact]
    public void Reset_StartsANewPage()
    {
        var o = Sample();
        var before = o.Version;
        o.Reset("https://outro.example/");
        var snap = o.Snapshot();
        Assert.Empty(snap.ThirdParties);
        Assert.Equal("outro.example", snap.Site);
        Assert.True(o.Version > before);
    }

    [Fact]
    public void TooManyHosts_CountOnlyInTheOverflow()
    {
        var o = new SiteObservatory();
        o.Reset("https://www.site.pt/");
        for (var i = 0; i < SiteObservatory.MaxHosts + 25; i++) o.Record($"https://h{i}.example/x", ResourceType.Image, ObservedVerdict.Allowed);
        o.Record("https://h0.example/again", ResourceType.Image, ObservedVerdict.Allowed);
        var snap = o.Snapshot();
        Assert.Equal(SiteObservatory.MaxHosts, snap.ThirdParties.Count);
        Assert.Equal(25, snap.Overflow);
        Assert.Equal(2, snap.ThirdParties[0].Requests);
    }

    // O registo corre na thread de rede do Chromium para cada pedido: um domínio já visto não pode reservar memória.
    // A medição é robusta ao JIT: o ciclo medido é compilado já otimizado (sem OSR a meio) e corre-se em várias rondas,
    // porque a promoção do Record de tier 0 para tier 1 pode reservar uns poucos KB (visto: 7184 B, ~1 em 9 execuções)
    // na ronda em que acontece. A garantia não enfraquece: um registo que reservasse por pedido reservaria em todas as
    // rondas (≥ 24 B × Calls cada); por isso o mínimo tem de ser 0 e nenhuma ronda pode crescer com o número de pedidos.
    [Fact]
    public void Record_ForAKnownHost_AllocatesNothing()
    {
        const int Rounds = 20, Calls = 10_000;
        var o = new SiteObservatory();
        o.Reset("https://www.site.pt/");
        const string url = "https://www.google-analytics.com/g/collect?v=2&tid=G-1";
        o.Record(url, ResourceType.Ping, ObservedVerdict.Blocked);
        var allocated = new long[Rounds];
        for (var r = 0; r < Rounds; r++) allocated[r] = AllocatedWhileRecording(o, url, Calls);
        Assert.Equal(0, allocated.Min());
        Assert.All(allocated, bytes => Assert.True(bytes < Calls, $"uma ronda reservou {bytes} B em {Calls} pedidos"));
        Assert.Equal(1 + Rounds * Calls, Assert.Single(o.Snapshot().ThirdParties).Requests);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining
        | System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static long AllocatedWhileRecording(SiteObservatory o, string url, int calls)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < calls; i++) o.Record(url, ResourceType.Ping, ObservedVerdict.Blocked);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void Record_IsSafeFromManyThreads()
    {
        var o = new SiteObservatory();
        o.Reset("https://www.site.pt/");
        Parallel.For(0, 8, t =>
        {
            for (var i = 0; i < 2_000; i++) o.Record($"https://h{i % 20}.example/x", ResourceType.Script, ObservedVerdict.Allowed);
        });
        var snap = o.Snapshot();
        Assert.Equal(20, snap.ThirdParties.Count);
        Assert.Equal(16_000, snap.ThirdParties.Sum(d => d.Requests));
    }

    [Fact]
    public void CompanyMap_IsEmbedded_AndCoversTheBigCompanies()
    {
        var map = CompanyMap.Default;
        Assert.True(map.DomainCount > 200);
        foreach (var company in new[] { "Google", "Meta", "Amazon", "Microsoft", "Criteo", "Adobe", "Oracle" })
            Assert.Contains(company, map.Companies);
        Assert.Equal("Google", map.OwnerOf("stats.g.doubleclick.net"));
        Assert.Equal("Meta", map.OwnerOf("connect.facebook.net"));
        Assert.Equal("Microsoft", map.OwnerOf("ib.adnxs.com"));
        Assert.Equal("Oracle", map.OwnerOf("tags.bluekai.com"));
        Assert.Null(map.OwnerOf("exemplo.pt"));
        Assert.Null(map.OwnerOf("notgoogle.com"));
    }

    // O mapa é nosso: cada domínio aparece uma vez e nenhum fica escondido por outro acima dele.
    [Fact]
    public void CompanyMap_HasNoDuplicatesNorShadowedEntries()
    {

        using var stream = typeof(CompanyMap).Assembly.GetManifestResourceStream("Eteris.Browser.Shields.owners.json")!;
        using var doc = System.Text.Json.JsonDocument.Parse(stream);
        Assert.Contains("Éteris", doc.RootElement.GetProperty("_sobre").GetString());
        var domains = doc.RootElement.GetProperty("empresas").EnumerateArray()
            .SelectMany(c => c.GetProperty("dominios").EnumerateArray().Select(d => d.GetString()!)).ToList();
        Assert.Equal(domains.Count, domains.Distinct().Count());
        foreach (var d in domains)
            Assert.DoesNotContain(domains, other => other != d && d.EndsWith("." + other, StringComparison.Ordinal));
    }

    [Fact]
    public void EveryTab_FeedsItsObservatoryToTheRequestHandler()
    {
        var parameter = typeof(PrivacyRequestHandler).GetConstructors().Single().GetParameters().Single(p => p.Name == "observatory");
        Assert.Equal(typeof(SiteObservatory), parameter.ParameterType);
        Assert.NotNull(new Eteris.Browser.Tabs.BrowserTab("https://exemplo.pt/").Observatory);
    }
}
