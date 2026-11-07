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

    // A janela privada tem a sua cache, que (sem DNS cifrado) só lê a cache do Windows e se esquece no fim da sessão
    // privada.
    [Fact]
    public void PrivateWindow_HasItsOwnCache()
    {
        Assert.Same(Cname.Cache, Cname.For(false));
        Assert.Same(Cname.PrivateCache, Cname.For(true));
        Assert.NotSame(Cname.Cache, Cname.PrivateCache);
        Assert.Equal(CnameSource.WindowsCacheOnly, Cname.SourceFor(secureDns: false, systemProxy: false, isPrivate: true));
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Shields", "CnameUncloak.cs"));
        Assert.Contains("PrivateCache { get; } = CnameCache.FromAsync(host => Resolve(host, isPrivate: true)", source);
        Assert.Contains("CnameSource.WindowsCacheOnly => BlockingLookups.Dns.TryRun(() => WindowsDns.QueryCname(host, cacheOnly: true))", source);
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

    // Revisão aos 50: a consulta bloqueante ao DNS do Windows corre em threads próprias, nunca no ThreadPool de que
    // dependem as continuações dos pedidos (o limite de 150 ms, os scriptlets).
    [Fact]
    public async Task Cache_NeverBlocksAThreadPoolThread()
    {
        bool? onPool = null;
        var cache = new CnameCache(_ => { onPool = Thread.CurrentThread.IsThreadPoolThread; return new CnameAnswer([], TimeSpan.Zero); },
            () => T0, new BlockingLookups(threads: 1, maxPending: 4));
        await cache.LookupAsync("metrics.jornal.pt");
        Assert.False(onPool);
    }

    // Com o DNS preso e a fila cheia, a consulta nem se faz: o pedido segue sem esperar e nada fica em cache.
    [Fact]
    public async Task BlockingLookups_WhenFull_SkipTheLookup()
    {
        var gate = new ManualResetEventSlim();
        var workers = new BlockingLookups(threads: 1, maxPending: 2);
        var cache = new CnameCache(_ => { gate.Wait(TimeSpan.FromSeconds(5)); return new CnameAnswer(["x.eulerian.net"], TimeSpan.FromMinutes(5)); },
            () => T0, workers);
        var first = cache.LookupAsync("a.jornal.pt");
        var second = cache.LookupAsync("b.jornal.pt");
        var third = cache.LookupAsync("c.jornal.pt");
        Assert.True(third.IsCompleted);
        Assert.Empty(await third);
        Assert.False(cache.TryGet("c.jornal.pt", out _));
        gate.Set();
        Assert.Equal(["x.eulerian.net"], await first);
        Assert.Equal(["x.eulerian.net"], await second);
        Assert.Equal(0, workers.Pending);
        // Livre outra vez, o nome que ficou de fora já se pergunta.
        Assert.Equal(["x.eulerian.net"], await cache.LookupAsync("c.jornal.pt"));
    }

    // Revisão aos 50: a cache e a quota não crescem sem fim (sessões de dias, abas que fecham).
    [Fact]
    public async Task Cache_DropsExpiredNamesAndStaysBounded()
    {
        var now = T0;
        var cache = CnameCache.FromAsync(_ => Task.FromResult<CnameAnswer?>(new CnameAnswer([], TimeSpan.FromMinutes(5))), () => now);
        await cache.LookupAsync("a.jornal.pt");
        Assert.Equal(1, cache.Count);
        now += TimeSpan.FromMinutes(6);
        Assert.False(cache.TryGet("a.jornal.pt", out _));
        Assert.Equal(0, cache.Count);
        for (var i = 0; i < CnameCache.MaxEntries + 50; i++) await cache.LookupAsync($"n{i}.jornal.pt");
        Assert.True(cache.Count <= CnameCache.MaxEntries);
        // Sem resposta (fila cheia, sem maneira segura de perguntar): nada em cache.
        var silent = CnameCache.FromAsync(_ => Task.FromResult<CnameAnswer?>(null), () => now);
        Assert.Empty(await silent.LookupAsync("a.jornal.pt"));
        Assert.Equal(0, silent.Count);
    }

    [Fact]
    public void Quota_ForgetsClosedBrowsersAndStaysBounded()
    {
        var quota = new CnameQuota(1);
        Assert.True(quota.TryTake(1, "https://jornal.pt/", "img.jornal.pt"));
        Assert.False(quota.TryTake(1, "https://jornal.pt/", "metrics.jornal.pt"));
        quota.Forget(1);
        Assert.Equal(0, quota.Browsers);
        for (var id = 0; id < CnameQuota.MaxBrowsers + 20; id++) quota.TryTake(id, "https://jornal.pt/", "img.jornal.pt");
        Assert.True(quota.Browsers <= CnameQuota.MaxBrowsers);
        var sleep = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "MainWindow.Sleep.cs"));
        Assert.Contains("ForgetCnameQuota(browser);", sleep);
        Assert.Contains("Shields.Cname.Quota.Forget(core.Identifier)", sleep);
        var window = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "MainWindow.xaml.cs"));
        Assert.Contains("ForgetCnameQuota(browser);", window);
    }

    // Revisão aos 50: com o DNS cifrado o Chromium resolve tudo por DoH e a cache do Windows nunca vê esses nomes; com
    // um proxy no Windows o DNS do sistema revelaria os nomes à rede local.
    [Theory]
    [InlineData(true, false, false, CnameSource.SecureDns)]
    [InlineData(true, true, true, CnameSource.SecureDns)]
    [InlineData(false, true, false, CnameSource.Off)]
    [InlineData(false, true, true, CnameSource.Off)]
    [InlineData(false, false, false, CnameSource.WindowsDns)]
    [InlineData(false, false, true, CnameSource.WindowsCacheOnly)]
    public void Source_FollowsSecureDnsAndTheProxy(bool secureDns, bool proxy, bool isPrivate, CnameSource expected)
        => Assert.Equal(expected, Cname.SourceFor(secureDns, proxy, isPrivate));

    [Theory]
    [InlineData(1, "proxy.empresa.pt:8080", null, true)]
    [InlineData(0, "proxy.empresa.pt:8080", null, false)]
    [InlineData(1, "", null, false)]
    [InlineData(0, null, "http://wpad/proxy.pac", true)]
    [InlineData(null, null, "  ", false)]
    [InlineData(null, null, null, false)]
    public void SystemProxy_ReadsTheWindowsSettings(int? enable, string? server, string? pac, bool expected)
        => Assert.Equal(expected, SystemProxy.From(enable, server, pac));

    [Fact]
    public void Doh_QueryIsTheWireFormatOfAnAQuestion()
    {
        var query = DohCname.BuildQuery("Metrics.Jornal.PT.")!;
        Assert.Equal(new byte[] { 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, query[..12]);
        Assert.Equal("metrics.jornal.pt", NameAt(query, 12).ToLowerInvariant());
        Assert.Equal(new byte[] { 0, 1, 0, 1 }, query[^4..]);
        const string Template = "https://dns.quad9.net/dns-query";
        var url = DohCname.QueryUrl(Template, "metrics.jornal.pt")!;
        Assert.StartsWith(Template + "?dns=", url);
        var encoded = url[(Template.Length + 5)..];
        Assert.DoesNotContain("=", encoded);
        Assert.DoesNotContain("+", encoded);
        Assert.DoesNotContain("/", encoded);
        Assert.Contains("&dns=", DohCname.QueryUrl("https://doh.example/q?x=1", "a.pt"));
        Assert.Null(DohCname.BuildQuery(""));
        Assert.Null(DohCname.BuildQuery("a..pt"));
        Assert.Null(DohCname.BuildQuery(new string('a', 64) + ".pt"));
        Assert.Null(DohCname.BuildQuery("açaí.pt"));
    }

    [Fact]
    public void Doh_AnswerGivesTheCnameChainWithCompression()
    {
        var answer = DohCname.ParseAnswer(Response(rcode: 0))!;
        Assert.Equal(["jornal.eulerian.net", "edge.eulerian.net"], answer.Chain);
        Assert.Equal(TimeSpan.FromSeconds(120), answer.Ttl);
        // Sem CNAME (só o endereço) e NXDOMAIN: respondido, sem cadeia.
        Assert.Empty(DohCname.ParseAnswer(Response(rcode: 0, cnames: false))!.Chain);
        Assert.Empty(DohCname.ParseAnswer(Response(rcode: 3, cnames: false))!.Chain);
        // NXDOMAIN do alvo (rastreador desligado ou que só responde a alguns): a cadeia vem na mesma.
        Assert.Equal(["jornal.eulerian.net", "edge.eulerian.net"], DohCname.ParseAnswer(Response(rcode: 3))!.Chain);
        // SERVFAIL, uma pergunta (não resposta), truncada, ponteiro em ciclo: falha.
        Assert.Null(DohCname.ParseAnswer(Response(rcode: 2)));
        var question = Response(rcode: 0);
        question[2] &= 0x7F;
        Assert.Null(DohCname.ParseAnswer(question));
        var full = Response(rcode: 0);
        Assert.Null(DohCname.ParseAnswer(full[..(full.Length - 3)]));
        var loop = Response(rcode: 0);
        var rdata = FirstCnameData(loop);
        loop[rdata] = 0xC0;
        loop[rdata + 1] = (byte)rdata;
        Assert.Null(DohCname.ParseAnswer(loop));
        Assert.Null(DohCname.ParseAnswer([1, 2, 3]));
    }

    [Fact]
    public void Texts_SayWhenTheProtectionIsPaused()
    {
        Assert.StartsWith("·  Rastreadores escondidos (CNAME): em pausa", CnameUncloak.ShieldLine([], enabled: true, CnameSource.Off));
        Assert.StartsWith("✓", CnameUncloak.ShieldLine([], enabled: true, CnameSource.SecureDns));
        Assert.StartsWith("·  Rastreadores escondidos (CNAME): desligado", CnameUncloak.ShieldLine([], enabled: false, CnameSource.Off));
        var handler = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "Shields", "PrivacyRequestHandler.cs"));
        Assert.Contains("Cname.SourceFor(isPrivate) == CnameSource.Off", handler);
        var shield = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "MainWindow.Shield.cs"));
        Assert.Contains("Data.Settings.Current.UncloakCname, Cname.SourceFor(IsPrivate))", shield);
    }

    private static string NameAt(byte[] message, int pos)
    {
        var labels = new List<string>();
        while (message[pos] != 0)
        {
            labels.Add(System.Text.Encoding.ASCII.GetString(message, pos + 1, message[pos]));
            pos += 1 + message[pos];
        }
        return string.Join('.', labels);
    }

    // Onde começam os dados do primeiro CNAME da resposta de Response (depois da pergunta e do cabeçalho do registo).
    private static int FirstCnameData(byte[] message) => 12 + "metrics.jornal.pt".Length + 2 + 4 + 2 + 10;

    // Resposta a «metrics.jornal.pt A»: CNAME jornal.eulerian.net (TTL 300), CNAME edge.eulerian.net (TTL 120, com
    // ponteiros para dentro do primeiro) e o endereço; ou só o endereço.
    private static byte[] Response(int rcode, bool cnames = true)
    {
        var m = new List<byte> { 0, 0, 0x81, (byte)(0x80 | rcode), 0, 1, 0, (byte)(cnames ? 3 : 1), 0, 0, 0, 0 };
        void Name(params string[] labels)
        {
            foreach (var l in labels)
            {
                m.Add((byte)l.Length);
                m.AddRange(System.Text.Encoding.ASCII.GetBytes(l));
            }
        }
        void U16(int v) { m.Add((byte)(v >> 8)); m.Add((byte)v); }
        void U32(int v) { U16(v >> 16); U16(v & 0xFFFF); }
        Name("metrics", "jornal", "pt");
        m.Add(0);
        U16(1);
        U16(1);
        var last = 12;
        if (cnames)
        {
            m.AddRange([0xC0, 12]);
            U16(5); U16(1); U32(300); U16(21);
            var first = m.Count;
            Name("jornal", "eulerian", "net");
            m.Add(0);
            m.AddRange([0xC0, (byte)first]);
            U16(5); U16(1); U32(120); U16(7);
            last = m.Count;
            Name("edge");
            m.AddRange([0xC0, (byte)(first + 7)]);
        }
        m.AddRange([0xC0, (byte)last]);
        U16(1); U16(1); U32(60); U16(4);
        m.AddRange([1, 2, 3, 4]);
        return m.ToArray();
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
