using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Shields;
using Eteris.Browser.Tests.Sync;

namespace Eteris.Browser.Tests;

// A exceção do Escudo desligada (ou o modo rigoroso ligado) num site: o renderer desse site tinha na cache de memória
// do Blink os scripts carregados enquanto o site estava sem proteção (o gpt.js do Público: private, max-age=900) e
// voltava a servi-los sem pedido ao processo do navegador, por isso sem passar pelo tratador do Escudo. Medido ao vivo
// na 0.2.17: o recarregar automático, o recarregar seguinte e uma visita nova na mesma aba corriam o gpt.js da cache de
// memória (requestServedFromCache, transferSize 0, googletag.apiReady true), também na outra aba do site. O que vem da
// janela (o motor) não se testa sem ela: o código fica fixado por texto.
public class ShieldTighteningTests
{
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public void Changed_SignalsOnlyARealTightening(bool wasProtected, bool isProtected, bool signalled)
    {
        var host = "t" + Guid.NewGuid().ToString("N") + ".example";
        var seen = 0;
        void On(string h) { if (h == host) seen++; }
        ShieldTightening.Tightened += On;
        try { ShieldTightening.Changed(host, wasProtected, isProtected); }
        finally { ShieldTightening.Tightened -= On; }
        Assert.Equal(signalled ? 1 : 0, seen);
    }

    [Theory]
    [InlineData("www.publico.pt", "https://www.publico.pt/", true)]
    [InlineData("www.publico.pt", "https://www.publico.pt/2026/11/13/sociedade/noticia/x-1", true)]
    [InlineData("publico.pt", "https://desporto.publico.pt/", true)]
    [InlineData("WWW.Publico.PT", "http://www.publico.pt/", true)]
    [InlineData("127.0.0.1", "http://127.0.0.1:18000/", true)]
    [InlineData("www.publico.pt", "https://expresso.pt/", false)]
    [InlineData("www.publico.pt", "https://publico.pt.exemplo.com/", false)]
    [InlineData("www.publico.pt", "eteris://newtab/", false)]
    [InlineData("www.publico.pt", "about:blank", false)]
    [InlineData("www.publico.pt", null, false)]
    public void Affects_EveryPageOfTheSite(string host, string? pageUrl, bool affected)
        => Assert.Equal(affected, ShieldTightening.Affects(host, pageUrl));

    // As quatro portas por onde a proteção de um site muda (o Escudo e o modo rigoroso, numa janela normal e numa
    // privada): o interruptor, a paleta, «Repor site» e a sincronização passam todas por elas.
    [Fact]
    public void AppData_SignalsEveryChange()
    {
        var data = Source("Data", "AppData.cs");
        Assert.Equal(4, Regex.Matches(data, @"ShieldTightening\.Changed\(").Count);
        foreach (var setter in new[] { "public static void SetShield(string host, bool on, bool isPrivate)", "public static void SetShield(string host, bool on)",
                     "public static void SetStrict(string host, bool on, bool isPrivate)", "public static void SetStrict(string host, bool on)" })
        {
            var body = Body(data, setter);
            Assert.Contains("ShieldTightening.Changed(", body);
        }
    }

    // Esvaziar é Network.setCacheDisabled(true) (o Blink chama MemoryCache::EvictResources no processo da página) e logo
    // false, sempre: uma falha a meio não pode deixar a página sem cache para sempre. Uma sessão DevTools própria e curta.
    [Fact]
    public void Eviction_DisablesTheCacheAndAlwaysTurnsItBackOn()
    {
        var evict = Source("Browser", "RendererCache.cs");
        Assert.Contains("using var client = browser.GetDevToolsClient();", evict);
        var on = evict.IndexOf("client.Network.SetCacheDisabledAsync(true)", StringComparison.Ordinal);
        var off = evict.IndexOf("client.Network.SetCacheDisabledAsync(false)", StringComparison.Ordinal);
        Assert.True(on > 0 && off > on);
        Assert.Contains("finally", evict[on..off]);
        Assert.Contains("PerfDiagnostics.Without(Infrastructure.PerfLayers.DevTools)", evict);
    }

    // Cada janela esvazia a cache das páginas abertas do site (abas, Espreitar, painéis), não só a da aba ativa: a outra
    // aba do Público, carregada com a exceção, servia o gpt.js da cache dela no recarregar seguinte.
    [Fact]
    public void Window_EvictsEveryOpenPageOfTheSite()
    {
        Assert.Contains("WatchShieldTightening();", Source("MainWindow.xaml.cs"));
        var shield = Source("MainWindow.Shield.cs");
        var watch = Body(shield, "private void WatchShieldTightening()");
        Assert.Contains("ShieldTightening.Tightened += OnTightened;", watch);
        Assert.Contains("Closed += (_, _) => ShieldTightening.Tightened -= OnTightened;", watch);
        Assert.Contains("_browsers.Values", watch);
        Assert.Contains("_glanceBrowser", watch);
        Assert.Contains("_webPanelBrowsers.Values", watch);
        Assert.Contains("ShieldTightening.Affects(host, browser.Address)", watch);
        Assert.Contains("RendererCache.EvictAsync(browser)", watch);
    }

    // O interruptor e a paleta recarregam a aba ativa sem cache quando a proteção volta: a página que recarrega não
    // espera pela limpeza (que é assíncrona) para pedir os scripts outra vez ao tratador.
    [Fact]
    public void Toggles_ReloadBypassingTheCacheWhenProtectionComesBack()
    {
        var shield = Source("MainWindow.Shield.cs");
        var toggle = Body(shield, "private void ShieldToggle_Click(object sender, RoutedEventArgs e)");
        Assert.Contains("ReloadActive(ignoreCache: on);", toggle);
        var strict = Body(shield, "private void StrictToggle_Click(object sender, RoutedEventArgs e)");
        Assert.Contains("ReloadActive(ignoreCache: on);", strict);
        var palette = Source("MainWindow.Palette.cs");
        Assert.Contains("AppData.SetShield(host, !on, IsPrivate);\n                    ReloadActive(ignoreCache: !on);", palette.ReplaceLineEndings("\n"));
    }

    // Voltar para uma página guardada na cache de Voltar/Avançar repõe-na tal como estava, sem pedidos (medido: o gpt.js
    // da reprodução local com googletag.apiReady true). Se foi deixada antes de o site ficar mais protegido, carrega-se
    // outra vez.
    [Fact]
    public void TightenedSince_APageLeftBeforeMoreProtection()
    {
        var host = "t" + Guid.NewGuid().ToString("N") + ".example";
        var page = "https://www." + host + "/artigo";
        var left = Environment.TickCount64;
        Assert.False(ShieldTightening.TightenedSince(page, left));
        Assert.False(ShieldTightening.TightenedSince(page, null));
        ShieldTightening.Changed(host, wasProtected: true, isProtected: false);
        Assert.False(ShieldTightening.TightenedSince(page, left));
        ShieldTightening.Changed(host, wasProtected: false, isProtected: true);
        Assert.True(ShieldTightening.TightenedSince(page, left));
        // Sem registo de quando foi deixada (a aba só guarda as últimas): carrega outra vez, pelo seguro.
        Assert.True(ShieldTightening.TightenedSince(page, null));
        Assert.False(ShieldTightening.TightenedSince(page, Environment.TickCount64 + 60_000));
        Assert.False(ShieldTightening.TightenedSince("https://outro" + host + "/", null));
        Assert.False(ShieldTightening.TightenedSince("eteris://newtab/", null));
    }

    [Fact]
    public void Window_ReloadsARestoredPageLeftBeforeMoreProtection()
    {
        var main = Source("MainWindow.xaml.cs");
        var leftAt = main.IndexOf("var leftAt = tab.LeftAt(arriving);", StringComparison.Ordinal);
        var leave = main.IndexOf("tab.LeavePage(leaving);", StringComparison.Ordinal);
        Assert.True(leftAt > 0 && leave > leftAt);
        Assert.Contains("if (restored && ShieldTightening.TightenedSince(arriving, leftAt)) ReloadRestored(tab, browser);", main);
        var reload = Body(Source("MainWindow.Shield.cs"), "private async void ReloadRestored(BrowserTab tab, CefSharp.Wpf.HwndHost.ChromiumWebBrowser browser)");
        Assert.Contains("RendererCache.EvictAsync(browser)", reload);
        Assert.Contains("await ScriptsReadyFor(tab, browser, tab.Address);", reload);
        Assert.Contains("browser.Reload(ignoreCache: true)", reload);
        Assert.Contains("catch (Exception)", reload);
    }

    private static string Source(params string[] parts)
        => File.ReadAllText(Path.Combine([GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", .. parts]));

    // O corpo de um método: da assinatura até à chaveta que fecha o primeiro bloco.
    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "não encontrado: " + signature);
        var open = source.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }
        return source[start..];
    }
}
