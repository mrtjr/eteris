using System.IO;
using System.Text.RegularExpressions;
using CefSharp;
using Eteris.Browser.Shields;
using static Eteris.Browser.Tests.PrivacyRequestHandlerTests;
using Cookie = CefSharp.Cookie;
using CookieFilter = Eteris.Browser.Shields.PrivacyRequestHandler.ThirdPartyCookieFilter;

namespace Eteris.Browser.Tests;

// Fase 0 das extensões: os pedidos sem aba (service worker, shared worker, o fetch com que um service worker reencaminha
// os pedidos da página, o service worker de uma extensão) passavam ao lado do Escudo. O tratador do contexto decide-os
// como os de uma aba, com a página de topo deduzida de quem fez o pedido. Prova ao vivo: docs/paridade.md, secção 8.
public class BrowserlessRequestsTests
{
    private sealed class Blocker(bool block) : IRequestBlocker
    {
        public string? LastDocument;
        public int Calls;

        public bool ShouldBlock(string requestUrl, string? pageUrl, string resourceType)
        {
            LastDocument = pageUrl;
            Calls++;
            return block && requestUrl.Contains("scorecardresearch", StringComparison.Ordinal);
        }
    }

    // isPrivate: true nos testes — o relatório de privacidade (estado global, GlobalStateCollection) não conta nada.
    private static (BrowserlessRequestHandler Handler, List<(string? Top, string Host)> Blocked) Create(Blocker blocker,
        Func<string, bool>? isProtected = null, Func<string, bool>? localNetworkAllowed = null)
    {
        var blocked = new List<(string?, string)>();
        var handler = new BrowserlessRequestHandler(() => blocker, isProtected ?? (_ => true), isPrivate: true, localNetworkAllowed,
            (top, host) => blocked.Add((top, host)));
        return (handler, blocked);
    }

    private static IRequest Request(string url, ResourceType type, List<(string, string)>? headers = null)
    {
        var (request, fake) = Fake<IRequest>.Create();
        fake.Members["get_Url"] = _ => url;
        fake.Members["get_ResourceType"] = _ => type;
        if (headers is not null)
            fake.Members["SetHeaderByName"] = args =>
            {
                headers.Add(((string)args![0]!, (string)args[1]!));
                return null;
            };
        return request;
    }

    [Theory]
    [InlineData("https://www.jornal.pt", null, "https://www.jornal.pt/")]
    [InlineData("http://127.0.0.1:8851", null, "http://127.0.0.1:8851/")]
    [InlineData("chrome-extension://dgaegnneoehohcmhcmmmhnigdkkkboeo", null, "chrome-extension://dgaegnneoehohcmhcmmmhnigdkkkboeo/")]
    // Origem opaca (iframe com sandbox, worker de data:): o site de ninguém, mesmo com referrer.
    [InlineData("null", "https://www.jornal.pt/a", null)]
    // Sem origem (pedido do próprio navegador): a do referrer http(s).
    [InlineData("", "https://www.jornal.pt/a?b=1", "https://www.jornal.pt/")]
    [InlineData("", "data:text/html,x", null)]
    [InlineData(null, null, null)]
    public void TopOf_IsTheOriginOfWhoAsked(string? initiator, string? referrer, string? expected)
        => Assert.Equal(expected, BrowserlessRequestHandler.TopOf(initiator, referrer));

    [Theory]
    [InlineData(ResourceType.Xhr)]
    [InlineData(ResourceType.SubResource)]
    [InlineData(ResourceType.Script)]
    public void WorkerRequestToATracker_IsBlockedAndCountedForTheWorkersSite(ResourceType type)
    {
        var blocker = new Blocker(block: true);
        var (handler, blocked) = Create(blocker);
        var result = handler.For("https://www.jornal.pt/").Decide(null, null, Request("https://sb.scorecardresearch.com/b?c1=2", type));
        Assert.Equal(CefReturnValue.Cancel, result);
        Assert.Equal("https://www.jornal.pt/", blocker.LastDocument);
        Assert.Equal([("https://www.jornal.pt/", "sb.scorecardresearch.com")], blocked);
    }

    [Fact]
    public void WorkerRequestToAFreeSite_PassesWithThePrivacyHeaders()
    {
        var headers = new List<(string, string)>();
        var (handler, blocked) = Create(new Blocker(block: true));
        var result = handler.For("https://www.jornal.pt/").Decide(null, null, Request("https://cdn.jornal.pt/a.js", ResourceType.Script, headers));
        Assert.Equal(CefReturnValue.Continue, result);
        Assert.Empty(blocked);
        foreach (var header in PrivacySignals.Headers) Assert.Contains((header.Name, header.Value), headers);
    }

    [Fact]
    public void ShieldOffOnTheWorkersSite_LetsItThrough()
    {
        var (handler, blocked) = Create(new Blocker(block: true), isProtected: host => host != "www.jornal.pt");
        Assert.Equal(CefReturnValue.Continue,
            handler.For("https://www.jornal.pt/").Decide(null, null, Request("https://sb.scorecardresearch.com/b", ResourceType.Xhr)));
        Assert.Empty(blocked);
    }

    // A navegação que um service worker reencaminha (respondWith(fetch(e.request))) não passa pelas listas nem pelos
    // saltos/HTTPS: isso é da navegação da aba.
    [Fact]
    public void BrowserlessMainFrame_OnlyGetsTheHeaders()
    {
        var blocker = new Blocker(block: true);
        var (handler, _) = Create(blocker);
        Assert.Equal(CefReturnValue.Continue,
            handler.For("https://www.jornal.pt/").Decide(null, null, Request("https://sb.scorecardresearch.com/", ResourceType.MainFrame)));
        Assert.Equal(0, blocker.Calls);
    }

    [Fact]
    public void ExtensionServiceWorker_StillGoesThroughTheLists()
    {
        var blocker = new Blocker(block: true);
        var (handler, blocked) = Create(blocker);
        var top = BrowserlessRequestHandler.TopOf("chrome-extension://dgaegnneoehohcmhcmmmhnigdkkkboeo", null);
        Assert.Equal(CefReturnValue.Cancel, handler.For(top).Decide(null, null, Request("http://sb.scorecardresearch.com/collect", ResourceType.Xhr)));
        Assert.Single(blocked);
    }

    [Fact]
    public void WorkerOfAPublicSite_DoesNotReachTheLocalNetwork_UnlessTheSiteMayDoSo()
    {
        var (handler, _) = Create(new Blocker(block: false));
        Assert.Equal(CefReturnValue.Cancel,
            handler.For("https://93.184.216.34/").Decide(null, null, Request("http://192.168.1.1/admin", ResourceType.Xhr)));
        // Um worker de uma página local fala com a rede local, como a página.
        Assert.Equal(CefReturnValue.Continue,
            handler.For("http://127.0.0.1:8851/").Decide(null, null, Request("http://127.0.0.1:6463/rpc", ResourceType.Xhr)));
        // Nenhum site (origem opaca): fechada.
        Assert.Equal(CefReturnValue.Cancel, handler.For(null).Decide(null, null, Request("http://127.0.0.1:6463/rpc", ResourceType.Xhr)));
        var (allowed, _) = Create(new Blocker(block: false), localNetworkAllowed: site => site == "93.184.216.34");
        Assert.Equal(CefReturnValue.Continue,
            allowed.For("https://93.184.216.34/").Decide(null, null, Request("http://192.168.1.1/admin", ResourceType.Xhr)));
    }

    [Fact]
    public void ThirdPartyCookies_AreJudgedAgainstTheWorkersSite()
    {
        var (handler, _) = Create(new Blocker(block: false));
        var filter = new CookieFilter(_ => true, isPrivate: true, handler.For("https://www.jornal.pt/").TopOf);
        var cookie = new Cookie { Name = "uid", Value = "9", Domain = "sb.scorecardresearch.com", Path = "/" };
        Assert.False(filter.CanSendCookie(null!, null!, null!, Request("https://sb.scorecardresearch.com/b", ResourceType.Image), cookie));
        Assert.True(filter.CanSendCookie(null!, null!, null!, Request("https://img.jornal.pt/a.png", ResourceType.Image), cookie));
        // Sem site (origem opaca), nenhum cookie de terceiros.
        var none = new CookieFilter(_ => true, isPrivate: true, handler.For(null).TopOf);
        Assert.False(none.CanSendCookie(null!, null!, null!, Request("https://img.jornal.pt/a.png", ResourceType.Image), cookie));
    }

    // O CefSharp embrulha um browser nulo (service worker) num objeto que fecha o Éteris à primeira leitura: o tratador
    // do contexto não lhe toca. Um tratador por site, reutilizado; a cache tem teto.
    [Fact]
    public void ContextHandler_NeverTouchesBrowserOrFrame_AndReusesOneHandlerPerSite()
    {
        var (handler, _) = Create(new Blocker(block: false));
        var (browser, browserFake) = Fake<IBrowser>.Create();
        foreach (var member in new[] { "get_Identifier", "get_MainFrame", "get_IsDisposed", "get_IsLoading", "GetHost", "get_FocusedFrame" })
            browserFake.Members[member] = _ => throw new AccessViolationException(member);
        var (frame, frameFake) = Fake<IFrame>.Create();
        foreach (var member in new[] { "get_IsValid", "get_Url", "get_IsMain", "get_Identifier", "get_Browser" })
            frameFake.Members[member] = _ => throw new AccessViolationException(member);
        IRequestContextHandler context = handler;
        var disable = false;
        var first = context.GetResourceRequestHandler(browser, frame, Request("https://sb.scorecardresearch.com/b", ResourceType.Xhr),
            false, false, "https://www.jornal.pt", ref disable);
        var again = context.GetResourceRequestHandler(browser, frame, Request("https://cdn.jornal.pt/b", ResourceType.Xhr),
            false, false, "https://www.jornal.pt", ref disable);
        var other = context.GetResourceRequestHandler(browser, frame, Request("https://cdn.jornal.pt/b", ResourceType.Xhr),
            false, false, "https://loja.pt", ref disable);
        Assert.NotNull(first);
        Assert.Same(first, again);
        Assert.NotSame(first, other);
        Assert.False(disable);
        for (var i = 0; i < BrowserlessRequestHandler.MaxTops + 50; i++) handler.For($"https://site{i}.pt/");
        Assert.InRange(handler.Tops, 1, BrowserlessRequestHandler.MaxTops);
    }

    // Todo o contexto de pedidos que o Éteris cria tem o tratador (o privado e o do Espreitar), e o do perfil instala-se
    // logo a seguir ao Cef.Initialize e liberta-se antes do fecho do motor.
    [Fact]
    public void EveryRequestContext_HasTheBrowserlessHandler()
    {
        var src = Path.Combine(RepoRoot(), "src", "Eteris.Browser");
        var contexts = 0;
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var text = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(text, @"new RequestContext\("))
            {
                var statement = text[match.Index..text.IndexOf(';', match.Index)];
                Assert.True(statement.Contains("BrowserlessRequests.Handler(", StringComparison.Ordinal)
                            || statement.Contains("Handler(isPrivate:", StringComparison.Ordinal), $"{Path.GetFileName(file)}: {statement}");
                contexts++;
            }
        }
        Assert.Equal(2, contexts);
        var app = File.ReadAllText(Path.Combine(src, "App.xaml.cs"));
        var ready = app[app.IndexOf("private static void OnCefReady()", StringComparison.Ordinal)..];
        Assert.Contains("Shields.BrowserlessRequests.Install();", ready[..ready.IndexOf("\n    }", StringComparison.Ordinal)]);
        Assert.True(app.IndexOf("Shields.BrowserlessRequests.Release();", StringComparison.Ordinal)
                    < app.IndexOf("Cef.PreShutdown();", StringComparison.Ordinal));
        var glance = File.ReadAllText(Path.Combine(src, "MainWindow.Glance.cs"));
        Assert.Contains("CreatePrivateContext(onBlocked: null)", glance);
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
