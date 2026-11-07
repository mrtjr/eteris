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
        if (RunNode(setup + "\n" + script + "\nprocess.stdout.write(String(globalThis.hit));") is not { } output) return;
        Assert.Equal(expected ? "true" : "false", output);
    }

    private static string? RunNode(string script)
    {
        var file = Path.Combine(Path.GetTempPath(), $"eteris-guard-{Guid.NewGuid():N}.js");
        File.WriteAllText(file, script);
        try
        {
            Process? node;
            try
            {
                node = Process.Start(new ProcessStartInfo("node", [file])
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                });
            }
            catch (System.ComponentModel.Win32Exception) { return null; } // Node não instalado: não há como verificar aqui.
            if (node is null) return null;
            var output = node.StandardOutput.ReadToEnd();
            var error = node.StandardError.ReadToEnd();
            node.WaitForExit(30_000);
            Assert.True(node.ExitCode == 0, error);
            return output;
        }
        finally { File.Delete(file); }
    }

    // Regras de código: o pedido da página nunca fica à espera (era o ~1 s por navegação), e os scripts nas páginas
    // antes dos da própria página só entram pelo ScriptletInjector.
    [Fact]
    public void PageRequest_NeverWaits()
    {
        var handler = File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", "Shields", "PrivacyRequestHandler.cs"));
        Assert.DoesNotContain("CefReturnValue.ContinueAsync", handler);
        Assert.DoesNotContain("Task.Delay", handler);
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
