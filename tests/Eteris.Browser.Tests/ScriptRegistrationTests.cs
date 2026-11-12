using System.Diagnostics;
using System.IO;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

// Registo dos scripts precoces (docs/motor.md, F2): sem esperas no pedido da página, scripts de site acumulados e
// protegidos pelo hostname da página de topo, e nunca um registo tirado a meio de uma navegação.
public class ScriptRegistrationTests
{
    [Fact]
    public void NewSite_IsRegistered_AndTheSameKeyIsNotRecalculated()
    {
        var registry = new HostScriptRegistry(4);
        Assert.False(registry.IsCurrent("a.pt", "k"));
        var change = registry.Plan("a.pt", "k", "script-a");
        Assert.Equal(new HostScriptRegistry.Change("a.pt", null, "script-a"), change);
        Assert.Null(registry.Registered("a.pt", "script-a", "id-1"));
        Assert.True(registry.IsCurrent("a.pt", "k"));
        Assert.Null(registry.Plan("a.pt", "k", "script-a"));
    }

    [Fact]
    public void ChangedScript_ReplacesTheOldRegistration()
    {
        var registry = new HostScriptRegistry(4);
        registry.Plan("a.pt", "escudo", "com-escudo");
        registry.Registered("a.pt", "com-escudo", "id-1");
        Assert.False(registry.IsCurrent("a.pt", "sem-escudo"));
        Assert.Equal(new HostScriptRegistry.Change("a.pt", "id-1", null), registry.Plan("a.pt", "sem-escudo", null));
        // Sem script, o site fica lembrado: não volta a ser calculado a cada link.
        Assert.True(registry.IsCurrent("a.pt", "sem-escudo"));
        Assert.Null(registry.Plan("a.pt", "sem-escudo", null));
    }

    [Fact]
    public void RegistrationThatArrivesAfterAChange_IsReturnedForRemoval()
    {
        var registry = new HostScriptRegistry(4);
        registry.Plan("a.pt", "1", "velho");
        registry.Plan("a.pt", "2", "novo");
        Assert.Equal("id-velho", registry.Registered("a.pt", "velho", "id-velho"));
        Assert.Null(registry.Registered("a.pt", "novo", "id-novo"));
    }

    [Fact]
    public void Evict_DropsTheOldestSites_ButNeverTheOneOnScreen()
    {
        var registry = new HostScriptRegistry(2);
        foreach (var host in new[] { "a.pt", "b.pt", "c.pt", "d.pt" })
        {
            registry.Plan(host, "k", "script-" + host);
            registry.Registered(host, "script-" + host, "id-" + host);
        }
        registry.IsCurrent("a.pt", "k"); // a.pt foi visitado de novo: passa a ser o mais recente
        Assert.Equal(["id-b.pt", "id-d.pt"], registry.Evict(keep: "c.pt"));
        Assert.Equal(2, registry.Count);
        Assert.True(registry.Contains("a.pt"));
        Assert.True(registry.Contains("c.pt"));
        Assert.Empty(registry.Evict(keep: "c.pt"));
    }

    [Theory]
    [InlineData("https://www.publico.pt/artigo", true)]
    [InlineData("http://exemplo.pt/", true)]
    [InlineData("eteris://newtab/", false)]
    [InlineData("about:blank", false)]
    [InlineData(null, false)]
    public void OnlyWebPagesHaveSiteScripts(string? url, bool expected) => Assert.Equal(expected, HostScriptRegistry.HostOf(url) is not null);

    [Fact]
    public void Guard_EmptyScript_IsNull() => Assert.Null(HostScriptRegistry.Guard("a.pt", ""));

    // O hostname e os protocolos vão como literais JSON: nada sai da string.
    [Fact]
    public void Guard_HostIsEscaped() => Assert.DoesNotContain("\"x\"; alert(1)", HostScriptRegistry.Guard("x\"; alert(1)", "f()")!);

    // Comportamento real do guarda, no Node: página de topo do site, iframe dentro dela (pela origem do topo), outro site.
    [Theory]
    [InlineData("https:", "www.bbc.com", null, true)]
    [InlineData("https:", "outro.pt", null, false)]
    [InlineData("https:", "anuncios.net", "https://www.bbc.com", true)]
    [InlineData("https:", "www.bbc.com", "https://outro.pt", false)]
    [InlineData("https:", "www.bbc.com", "null", false)]
    public void Guard_RunsOnlyUnderTheSitesTopPage(string protocol, string host, string? topOrigin, bool runs)
        => AssertRuns(HostScriptRegistry.Guard("www.bbc.com", "globalThis.hit = true;")!, protocol, host, topOrigin, runs);

    [Theory]
    [InlineData("https:", "a.pt", null, true)]
    [InlineData("eteris:", "newtab", null, false)]
    [InlineData("about:", "", "https://a.pt", true)]
    public void ForProtocols_RunsOnlyOnThoseProtocols(string protocol, string host, string? topOrigin, bool runs)
        => AssertRuns(HostScriptRegistry.ForProtocols(["http:", "https:"], "globalThis.hit = true;"), protocol, host, topOrigin, runs);

    private static void AssertRuns(string script, string protocol, string host, string? topOrigin, bool expected)
    {
        var setup = $$"""
            globalThis.window = globalThis;
            window.top = {{(topOrigin is null ? "window" : "{}")}};
            globalThis.location = { protocol: {{System.Text.Json.JsonSerializer.Serialize(protocol)}}, hostname: {{System.Text.Json.JsonSerializer.Serialize(host)}},
              ancestorOrigins: {{(topOrigin is null ? "[]" : "[" + System.Text.Json.JsonSerializer.Serialize(topOrigin) + "]")}} };
            globalThis.hit = false;
            """;
        var output = RunNode(setup + "\n" + script + "\nprocess.stdout.write(String(globalThis.hit));");
        Assert.Equal(expected ? "true" : "false", output);
    }

    private static string RunNode(string script) => NodeRunner.Run(script);

    // Regras de código: o pedido da página nunca fica à espera (era o ~1 s por navegação), e os scripts nas páginas
    // antes dos da própria página só entram pelo ScriptletInjector. A única espera que resta no tratador é a do CNAME
    // (sub-recursos, limitada a CnameUncloak.MaxWait), nunca a da página.
    [Fact]
    public void PageRequest_NeverWaits()
    {
        var handler = File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", "Shields", "PrivacyRequestHandler.cs")).Replace("\r\n", "\n");
        var start = handler.IndexOf("if (request.ResourceType == ResourceType.MainFrame)\n", StringComparison.Ordinal);
        var end = handler.IndexOf("IsNetwork(request.Url)", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        var page = handler[start..end];
        Assert.DoesNotContain("ContinueAsync", page);
        Assert.DoesNotContain("Task.Delay", page);
        // O ramo da página principal vive em DecideMainFrame: também aí nada espera.
        var method = handler.IndexOf("private CefReturnValue DecideMainFrame(", StringComparison.Ordinal);
        Assert.True(method > 0);
        var body = handler[method..handler.IndexOf("\n        }\n", method, StringComparison.Ordinal)];
        Assert.DoesNotContain("ContinueAsync", body);
        Assert.DoesNotContain("Task.Delay", body);
        Assert.DoesNotContain("onNavigate", handler);
        // Só dois sub-recursos podem esperar, e com limite: o CNAME e o endereço de uma página de nome público que tenta
        // chegar à rede local (Revisão aos 50).
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(handler, @"CefReturnValue\.ContinueAsync").Count);
        Assert.Contains("Task.WhenAny(lookup, Task.Delay(CnameUncloak.MaxWait))", handler);
        Assert.Contains("Task.WhenAny(lookup, Task.Delay(LocalNetwork.MaxWait))", handler);
        Assert.True(Eteris.Browser.Shields.LocalNetwork.MaxWait <= TimeSpan.FromMilliseconds(300));
    }

    [Fact]
    public void EarlyScripts_AreRegisteredOnlyByTheInjector()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(Root, "src", "Eteris.Browser"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && Path.GetFileName(f) != "ScriptletInjector.cs")
            .Where(f => File.ReadAllText(f).Contains("AddScriptToEvaluateOnNewDocument", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(offenders);
    }

    private static readonly string Root = FindRoot();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }
}
