using System.IO;
using CefSharp;
using Eteris.Browser.Data;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

// B8 «Desmascarar CNAME»: quem é candidato, a decisão a partir da cadeia CNAME, a cache com TTL e o limite por página.
public class CnameUncloakTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);

    // Lista de teste: bloqueia tudo o que vá para eulerian.net ou at-o.net.
    private static bool Lists(string url, string? page, string type)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Host.EndsWith("eulerian.net", StringComparison.Ordinal) || u.Host.EndsWith("at-o.net", StringComparison.Ordinal));

    [Theory]
    [InlineData("https://www.jornal.pt/artigo", "https://metrics.jornal.pt/col.js", ResourceType.Script, true)]
    [InlineData("https://www.jornal.pt/artigo", "https://jornal.pt/pixel.gif", ResourceType.Image, true)]
    [InlineData("https://www.loja.com.br/", "https://stats.loja.com.br/e", ResourceType.Xhr, true)]
    [InlineData("https://www.jornal.pt/artigo", "https://www.jornal.pt/app.js", ResourceType.Script, false)]
    [InlineData("https://www.jornal.pt/artigo", "https://cdn.outro.pt/app.js", ResourceType.Script, false)]
    [InlineData("https://www.jornal.pt/artigo", "https://metrics.jornal.pt/", ResourceType.MainFrame, false)]
    [InlineData("https://www.jornal.pt/artigo", "https://fonts.jornal.pt/a.woff2", ResourceType.FontResource, false)]
    [InlineData("https://www.jornal.pt/artigo", "wss://metrics.jornal.pt/s", ResourceType.Xhr, false)]
    [InlineData("http://192.168.1.1/", "http://192.168.1.2/x.js", ResourceType.Script, false)]
    [InlineData("eteris://newtab/", "https://metrics.jornal.pt/col.js", ResourceType.Script, false)]
    [InlineData(null, "https://metrics.jornal.pt/col.js", ResourceType.Script, false)]
    public void IsCandidate_OnlySubresourcesOfOtherNamesOfTheSameSite(string? page, string request, ResourceType type, bool expected)
        => Assert.Equal(expected, CnameUncloak.IsCandidate(page, request, type));

    [Fact]
    public void CloakedTracker_FindsTheListedNameBehindTheAlias()
    {
        var target = CnameUncloak.CloakedTracker("https://metrics.jornal.pt/col.js?v=2", "https://www.jornal.pt/", "script",
            ["jornal.eulerian.net"], Lists);
        Assert.Equal("jornal.eulerian.net", target);
    }

    [Fact]
    public void CloakedTracker_AsksTheListsWithTheSamePathAndType()
    {
        string? seen = null;
        CnameUncloak.CloakedTracker("https://metrics.jornal.pt/col.js?v=2", "https://www.jornal.pt/", "script", ["jornal.eulerian.net."],
            (url, page, type) => { seen = $"{url} {page} {type}"; return false; });
        Assert.Equal("https://jornal.eulerian.net/col.js?v=2 https://www.jornal.pt/ script", seen);
    }

    [Fact]
    public void CloakedTracker_WalksTheWholeChainAndSkipsInternalAliases()
    {
        Assert.Equal("x.at-o.net", CnameUncloak.CloakedTracker("https://s.jornal.pt/a", "https://jornal.pt/", "image",
            ["edge.jornal.pt", "jornal.cdn.example", "x.at-o.net"], Lists));
        // Um CDN que as listas não conhecem não bloqueia nada.
        Assert.Null(CnameUncloak.CloakedTracker("https://img.jornal.pt/a.png", "https://jornal.pt/", "image",
            ["jornal.cdn.cloudflare.net"], Lists));
        Assert.Null(CnameUncloak.CloakedTracker("https://img.jornal.pt/a.png", "https://jornal.pt/", "image", [], Lists));
        // Um alias do próprio site nunca é perguntado às listas (não deixa a própria lista bloquear o site por engano).
        Assert.Null(CnameUncloak.CloakedTracker("https://a.eulerian.net/x", "https://eulerian.net/", "script", ["b.eulerian.net"], Lists));
        // Nomes estranhos na resposta não se usam.
        Assert.Null(CnameUncloak.CloakedTracker("https://s.jornal.pt/a", "https://jornal.pt/", "image", ["", "não é nome", "1.2.3.4"], Lists));
    }

    [Fact]
    public void Label_ShowsTheVisibleNameAndTheTracker()
        => Assert.Equal("metrics.jornal.pt → jornal.eulerian.net", CnameUncloak.Label("metrics.jornal.pt", "jornal.eulerian.net"));

    [Fact]
    public async Task Cache_AnswersFromMemoryUntilTheTtlRunsOut()
    {
        var now = T0;
        var calls = 0;
        var cache = new CnameCache(_ => { calls++; return new CnameAnswer(["jornal.eulerian.net"], TimeSpan.FromMinutes(5)); }, () => now);
        Assert.False(cache.TryGet("metrics.jornal.pt", out _));
        Assert.Equal(["jornal.eulerian.net"], await cache.LookupAsync("metrics.jornal.pt"));
        Assert.True(cache.TryGet("metrics.jornal.pt", out var chain));
        Assert.Equal(["jornal.eulerian.net"], chain);
        await cache.LookupAsync("metrics.jornal.pt");
        Assert.Equal(1, calls);
        now = T0.AddMinutes(5).AddSeconds(1);
        Assert.False(cache.TryGet("metrics.jornal.pt", out _));
        await cache.LookupAsync("metrics.jornal.pt");
        Assert.Equal(2, calls);
        Assert.Equal(2, cache.Lookups);
    }

    [Fact]
    public async Task Cache_ClampsTheTtlAndRemembersFailuresBriefly()
    {
        var now = T0;
        var answers = new Queue<CnameAnswer>(
        [
            new(["a.eulerian.net"], TimeSpan.FromSeconds(5)),
            new(["b.eulerian.net"], TimeSpan.FromDays(1)),
            new([], TimeSpan.FromDays(1)),
            CnameAnswer.Failed,
        ]);
        var cache = new CnameCache(_ => answers.Dequeue(), () => now);
        await cache.LookupAsync("curto.jornal.pt");
        await cache.LookupAsync("longo.jornal.pt");
        await cache.LookupAsync("sem.jornal.pt");
        await cache.LookupAsync("falha.jornal.pt");
        now = T0 + CnameCache.MinTtl - TimeSpan.FromSeconds(1);
        Assert.True(cache.TryGet("curto.jornal.pt", out _));
        Assert.True(cache.TryGet("falha.jornal.pt", out _));
        now = T0 + CnameCache.MinTtl + TimeSpan.FromSeconds(1);
        Assert.False(cache.TryGet("curto.jornal.pt", out _));
        Assert.False(cache.TryGet("falha.jornal.pt", out _));
        now = T0 + CnameCache.NoCnameTtl + TimeSpan.FromSeconds(1);
        Assert.False(cache.TryGet("sem.jornal.pt", out _));
        Assert.True(cache.TryGet("longo.jornal.pt", out _));
        now = T0 + CnameCache.MaxTtl + TimeSpan.FromSeconds(1);
        Assert.False(cache.TryGet("longo.jornal.pt", out _));
    }

    [Fact]
    public async Task Cache_OneQueryInFlightPerName()
    {
        var gate = new ManualResetEventSlim();
        var calls = 0;
        var cache = new CnameCache(_ => { Interlocked.Increment(ref calls); gate.Wait(TimeSpan.FromSeconds(5)); return new CnameAnswer([], TimeSpan.Zero); },
            () => T0);
        var first = cache.LookupAsync("metrics.jornal.pt");
        var second = cache.LookupAsync("metrics.jornal.pt");
        gate.Set();
        await Task.WhenAll(first, second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Cache_AResolverThatThrowsIsAFailure()
    {
        var cache = new CnameCache(_ => throw new InvalidOperationException("dns"), () => T0);
        Assert.Empty(await cache.LookupAsync("metrics.jornal.pt"));
    }

    [Fact]
    public void Quota_LimitsNewQueriesPerPage()
    {
        var quota = new CnameQuota(2);
        Assert.True(quota.TryTake(1, "https://jornal.pt/a", "img.jornal.pt"));
        Assert.True(quota.TryTake(1, "https://jornal.pt/a", "metrics.jornal.pt"));
        Assert.False(quota.TryTake(1, "https://jornal.pt/a", "cdn.jornal.pt"));
        // Outra aba e outra página começam do zero.
        Assert.True(quota.TryTake(2, "https://jornal.pt/a", "cdn.jornal.pt"));
        Assert.True(quota.TryTake(1, "https://jornal.pt/b", "cdn.jornal.pt"));
        Assert.False(new CnameQuota(0).TryTake(1, "https://jornal.pt/", "img.jornal.pt"));
    }

    // Revisão aos 50: 30 imagens do mesmo alias gastam uma só consulta; o rastreador escondido que vem depois ainda
    // tem quota.
    [Fact]
    public void Quota_CountsDistinctNames_NotRequests()
    {
        var quota = new CnameQuota(CnameUncloak.PageQuota);
        for (var i = 0; i < 30; i++) Assert.True(quota.TryTake(1, "https://www.jornal.pt/", "img.jornal.pt"));
        Assert.True(quota.TryTake(1, "https://www.jornal.pt/", "metrics.jornal.pt"));
    }

    // A janela privada tem a sua cache, que só lê a cache do Windows e se esquece no fim da sessão privada.
    [Fact]
    public void PrivateWindow_HasItsOwnCache()
    {
        Assert.Same(Cname.Cache, Cname.For(false));
        Assert.Same(Cname.PrivateCache, Cname.For(true));
        Assert.NotSame(Cname.Cache, Cname.PrivateCache);
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Shields", "CnameUncloak.cs"));
        Assert.Contains("PrivateCache { get; } = new(host => WindowsDns.QueryCname(host, cacheOnly: true)", source);
        var handler = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Shields", "PrivacyRequestHandler.cs"));
        Assert.Contains("var cache = Cname.For(isPrivate);", handler);
        Assert.DoesNotContain("Cname.Cache.", handler);
        var ending = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "MainWindow.Private.cs"));
        Assert.Equal(2, ending.Split("Shields.Cname.PrivateCache.Clear();").Length - 1);
    }

    [Fact]
    public void WindowsDns_CacheOnlyQueryOfAnUnknownNameIsQuiet()
        => Assert.Empty(WindowsDns.QueryCname("eteris-teste-que-nao-existe.invalid", cacheOnly: true).Chain);

    [Fact]
    public void Setting_IsOnByDefaultAndCanBeTurnedOff()
    {
        Assert.True(new UserSettings().UncloakCname);
        Assert.False(new UserSettings().With("cname", "false")!.UncloakCname);
    }

    // Regras duras: o DNS só pelo dnsapi.dll (sem pacotes nem HttpClient) e o pedido nunca espera pelo DNS na thread
    // de rede (a espera é pelo IRequestCallback, com limite).
    [Fact]
    public void Source_QueriesTheWindowsDnsAndNeverWaitsOnTheNetworkThread()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Shields", "CnameUncloak.cs"));
        Assert.Contains("\"dnsapi.dll\", EntryPoint = \"DnsQuery_W\"", source);
        Assert.DoesNotContain("HttpClient", source);
        Assert.DoesNotContain("System.Net.Dns", source);
        var handler = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Shields", "PrivacyRequestHandler.cs"));
        Assert.Contains("Task.WhenAny(lookup, Task.Delay(CnameUncloak.MaxWait))", handler);
        Assert.DoesNotContain(".Result;", handler);
        Assert.DoesNotContain(".Wait(", handler);
        Assert.True(CnameUncloak.MaxWait <= TimeSpan.FromMilliseconds(250));
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
