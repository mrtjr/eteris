using CefSharp;
using CefSharp.Wpf.HwndHost;
using Eteris.Browser.Browser;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// Auto-teste de privacidade: quando a página de teste acaba de carregar, as medições correm pelo DevTools (PageScript),
// o veredicto é decidido em SelfTest.Grade com o que o «servidor» do Éteris recebeu e os resultados voltam à página.
public partial class MainWindow
{
    private static async Task RunSelfTestAsync(BrowserTab tab, ChromiumWebBrowser browser, string url)
    {
        var result = await PageScript.EvaluateAsync(browser, SelfTest.MeasureScript, TimeSpan.FromSeconds(15), asyncBody: true);
        if (browser.IsDisposed || browser.GetMainFrame()?.Url != url) return;
        if (!result.Success || SelfTest.Parse(result.Result) is not { } measured)
        {
            PageScript.Run(browser, SelfTest.FailedScript(result.Message ?? "a página não respondeu"));
            return;
        }
        // O registo da rede local chega pela thread da janela: dá-lhe um instante.
        await Task.Delay(150);
        var lanBlocked = tab.LocalNetworkBlocked.Any(p => p.Key.EndsWith("127.0.0.1:9", StringComparison.Ordinal));
        // A política de IP do WebRTC em vigor no perfil desta aba (normal ou privado), lida na thread do motor.
        string? webRtcPolicy = null;
        try
        {
            var context = browser.GetBrowserHost()?.RequestContext;
            if (context is not null)
                webRtcPolicy = await Cef.UIThreadTaskFactory.StartNew(() => context.GetPreference(Shields.PrivacyPreferences.WebRtcPreference) as string);
        }
        catch (Exception) { }
        if (browser.IsDisposed || browser.GetMainFrame()?.Url != url) return;
        var run = measured.Run;
        var checks = SelfTest.Grade(measured, SelfTest.Got("main"), SelfTest.Got(run + "/c1"), SelfTest.Got(run + "/c3"),
            SelfTest.Got(run + "/ref"), lanBlocked, webRtcPolicy);
        PageScript.Run(browser, SelfTest.RenderScript(checks));
    }
}
