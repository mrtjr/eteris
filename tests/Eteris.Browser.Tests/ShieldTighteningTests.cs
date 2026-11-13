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

    // O recorte ao vivo é uma janela à parte, que fica aberta e recarrega sozinha: também larga a cache do site.
    [Fact]
    public void Clip_EvictsItsPageOfTheSite()
    {
        var clip = Source("ClipWindow.xaml.cs");
        Assert.Contains("Shields.ShieldTightening.Tightened += OnTightened;", clip);
        Assert.Contains("Closed += (_, _) => Shields.ShieldTightening.Tightened -= OnTightened;", clip);
        Assert.Contains("Shields.ShieldTightening.Affects(host, _browser.Address)", clip);
        Assert.Contains("RendererCache.EvictAsync(_browser)", clip);
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
    // da reprodução local com googletag.apiReady true). Se o documento carregou antes de o site ficar mais protegido,
    // carrega-se outra vez.
    [Fact]
    public void TightenedSince_APageLoadedBeforeMoreProtection()
    {
        var host = "t" + Guid.NewGuid().ToString("N") + ".example";
        var page = "https://www." + host + "/artigo";
        var loaded = Environment.TickCount64;
        Assert.False(ShieldTightening.TightenedSince(page, loaded));
        Assert.False(ShieldTightening.TightenedSince(page, null));
        ShieldTightening.Changed(host, wasProtected: true, isProtected: false);
        Assert.False(ShieldTightening.TightenedSince(page, loaded));
        ShieldTightening.Changed(host, wasProtected: false, isProtected: true);
        Assert.True(ShieldTightening.TightenedSince(page, loaded));
        // Sem registo de quando carregou (a aba só guarda as últimas): carrega outra vez, pelo seguro.
        Assert.True(ShieldTightening.TightenedSince(page, null));
        Assert.False(ShieldTightening.TightenedSince(page, Environment.TickCount64 + 60_000));
        Assert.False(ShieldTightening.TightenedSince("https://outro" + host + "/", null));
        Assert.False(ShieldTightening.TightenedSince("eteris://newtab/", null));
    }

    // Carregada com menos proteção e deixada só depois de a proteção mudar (a outra aba do site, ou a mudança veio das
    // Definições, da sincronização ou de outra janela, e nenhuma aba recarregou): o documento que a cache de Voltar/Avançar
    // repõe é o de antes da mudança, por isso também carrega outra vez.
    [Fact]
    public void TightenedSince_APageLoadedBeforeButLeftAfterMoreProtection()
    {
        var host = "t" + Guid.NewGuid().ToString("N") + ".example";
        var page = "https://www." + host + "/artigo";
        var tab = new Tabs.BrowserTab(page);
        var loaded = Environment.TickCount64;
        ShieldTightening.Changed(host, wasProtected: false, isProtected: true);
        Thread.Sleep(40);
        tab.LeavePage(page, loaded);
        Assert.True(ShieldTightening.TightenedSince(page, tab.LoadedAt(page)));
        // O documento novo no mesmo endereço (a aba recarregou com a proteção) já não.
        Thread.Sleep(40);
        tab.LeavePage(page, Environment.TickCount64);
        Assert.False(ShieldTightening.TightenedSince(page, tab.LoadedAt(page)));
    }

    // Uma mudança numa janela privada só vale nas janelas privadas e é esquecida quando a sessão privada termina
    // (PrivateSession.End): o nome do site não fica em memória, e um Voltar numa janela normal não recarrega por causa
    // dela. Uma mudança numa janela normal vale também nas privadas (que seguem a escolha normal sem uma delas).
    [Fact]
    public void TightenedSince_APrivateChangeStaysPrivateAndIsForgotten()
    {
        var host = "t" + Guid.NewGuid().ToString("N") + ".example";
        var page = "https://www." + host + "/artigo";
        var loaded = Environment.TickCount64;
        ShieldTightening.Changed(host, wasProtected: false, isProtected: true, isPrivate: true);
        Assert.False(ShieldTightening.TightenedSince(page, loaded, isPrivate: false));
        Assert.False(ShieldTightening.TightenedSince(page, null, isPrivate: false));
        Assert.True(ShieldTightening.TightenedSince(page, loaded, isPrivate: true));
        ShieldTightening.ForgetPrivate();
        Assert.False(ShieldTightening.TightenedSince(page, loaded, isPrivate: true));

        var normal = "t" + Guid.NewGuid().ToString("N") + ".example";
        var normalPage = "https://" + normal + "/";
        ShieldTightening.Changed(normal, wasProtected: false, isProtected: true);
        Assert.True(ShieldTightening.TightenedSince(normalPage, loaded, isPrivate: true));
        ShieldTightening.ForgetPrivate();
        Assert.True(ShieldTightening.TightenedSince(normalPage, loaded, isPrivate: true));
        Assert.True(ShieldTightening.TightenedSince(normalPage, loaded, isPrivate: false));
    }

    // As portas da janela privada marcam a mudança como privada, e a janela pergunta pela dela.
    [Fact]
    public void PrivateChanges_AreMarkedPrivate()
    {
        var data = Source("Data", "AppData.cs");
        Assert.Contains("ShieldTightening.Changed(host, was, on, isPrivate: true);",
            Body(data, "public static void SetShield(string host, bool on, bool isPrivate)"));
        Assert.Contains("ShieldTightening.Changed(host, was, on, isPrivate: true);",
            Body(data, "public static void SetStrict(string host, bool on, bool isPrivate)"));
        Assert.Contains("ShieldTightening.TightenedSince(arriving, loadedAt, IsPrivate)", Source("MainWindow.xaml.cs"));
    }

    // Cada commit guarda a hora em que o documento carregou (no mesmo sítio do endereço confirmado), e a página deixada
    // leva a hora dela: o pedido da página nova e o commit sem pedido passam-na a LeavePage.
    [Fact]
    public void Window_RemembersWhenEachDocumentLoaded()
    {
        var main = Source("MainWindow.xaml.cs");
        Assert.Equal(2, Regex.Matches(main, @"Volatile\.Read\(ref committedAt\)").Count);
        Assert.Contains("Volatile.Write(ref committedAt, Environment.TickCount64);", main);
        Assert.Contains("tab.LeavePage(leaving, leavingLoadedAt)", main);
        Assert.DoesNotContain("tab.LeavePage(leaving);", main);
        Assert.DoesNotContain("LeftAt", main);
    }

    [Fact]
    public void Window_ReloadsARestoredPageLoadedBeforeMoreProtection()
    {
        var main = Source("MainWindow.xaml.cs");
        var loadedAt = main.IndexOf("var loadedAt = tab.LoadedAt(arriving);", StringComparison.Ordinal);
        var leave = main.IndexOf("tab.LeavePage(leaving, leavingLoadedAt);", StringComparison.Ordinal);
        Assert.True(loadedAt > 0 && leave > loadedAt);
        // O endereço que chegou, não o da aba (que pode ainda não ter mudado quando isto corre).
        Assert.Contains("if (restored && ShieldTightening.TightenedSince(arriving, loadedAt, IsPrivate) && ShieldOnAt(arriving)) ReloadRestored(tab, browser, arriving);", main);
        var reload = Body(Source("MainWindow.Shield.cs"),
            "private async void ReloadRestored(BrowserTab tab, CefSharp.Wpf.HwndHost.ChromiumWebBrowser browser, string url)");
        Assert.Contains("RendererCache.EvictAsync(browser)", reload);
        Assert.Contains("await ScriptsReadyFor(tab, browser, url);", reload);
        Assert.Contains("browser.Reload(ignoreCache: true)", reload);
        Assert.Contains("catch (Exception)", reload);
    }

    // A proteção do site saiu outra vez depois da mudança (a exceção ligada de novo): com o Escudo desligado no site o
    // recarregar não bloquearia nada e só perdia o que estava escrito na página. O modo rigoroso não conta sem o Escudo
    // (o tratador deixa passar tudo antes de o ver: PrivacyRequestHandler).
    [Fact]
    public void Window_DoesNotReloadARestoredPageWhenTheShieldIsOffThere()
    {
        var shield = Source("MainWindow.Shield.cs");
        var on = Body(shield, "private bool ShieldOnAt(string url)");
        Assert.Contains("ShieldOn(uri.Host)", on);
        var handler = Source("Shields", "PrivacyRequestHandler.cs");
        var off = handler.IndexOf("!isProtected(page.Host))", StringComparison.Ordinal);
        var strict = handler.IndexOf("Data.AppData.IsStrict(top.Host, isPrivate)", StringComparison.Ordinal);
        Assert.True(off > 0 && strict > off);
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
