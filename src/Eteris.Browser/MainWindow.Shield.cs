using System.Windows;
using System.Windows.Media;
using CefSharp;
using Eteris.Browser.Data;
using Eteris.Browser.Shields;
using Eteris.Browser.Browser;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

public partial class MainWindow
{
    private static string? WebHost(string? address)
        => address is not null && Uri.TryCreate(address, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.Host
            : null;

    // Regras cosméticas específicas do site (##… das listas), registadas para correr antes dos scripts da página,
    // quando os anúncios ainda não apareceram. Vazio sem escudo ou sem regras para o site.
    // O Escudo neste site, para esta janela (numa janela privada conta também o que se decidiu nela: AppData).
    private bool ShieldOn(string host) => AppData.IsShieldOn(host, IsPrivate);

    private string SiteCosmeticScript(string url)
    {
        if (WebHost(url) is not { } host || !ShieldOn(host) || ShieldEngine.Cosmetic is not { } engine) return string.Empty;
        var script = CosmeticScripts.InjectEarly(engine.UrlCss(url));
        return script.Length == 0 ? string.Empty : script + "\n;\n";
    }

    // Um pedido sem aba (service worker, shared worker: Shields/BrowserlessRequests.cs) bloqueado pelo Escudo conta na
    // aba que mostra o site de onde partiu: a ativa, se for desse site, senão a primeira; a janela ativa primeiro. Sem
    // aba desse site (o worker sobreviveu à página), fica só no total do Escudo. «only»: a janela privada dona do contexto.
    internal static void RecordBrowserlessBlock(string? top, string host, MainWindow? only = null)
    {
        if (TopDocument.SiteKey(top) is not { } site) return;
        var windows = only is not null ? [only] : NormalWindows().OrderByDescending(w => w.IsActive).ToList();
        foreach (var window in windows)
        {
            var tabs = window.Tabs;
            var tab = tabs.Active is { } active && TopDocument.SiteKey(active.Address) == site
                ? active
                : tabs.Tabs.FirstOrDefault(t => TopDocument.SiteKey(t.Address) == site);
            if (tab is null) continue;
            tab.RecordBlocked(host);
            return;
        }
    }

    private void UpdateShieldButton()
    {
        var tab = Tabs.Active;
        var host = WebHost(tab?.Address);
        var on = host is null || ShieldOn(host);
        var count = tab?.BlockedCount ?? 0;

        ShieldBadge.Visibility = count > 0 && on ? Visibility.Visible : Visibility.Collapsed;
        ShieldCount.Text = count > 99 ? "99+" : count.ToString();
        ShieldGlyph.Foreground = (Brush)FindResource(on ? "TextBrush" : "TextFaintBrush");
        var cloaked = tab?.CloakedTrackers.Count ?? 0;
        ShieldButton.ToolTip = !on ? "Escudo desligado neste site"
            : (count == 1 ? "1 rastreador bloqueado" : $"{count} rastreadores bloqueados")
              + (cloaked == 0 ? string.Empty : cloaked == 1 ? " (1 escondido)" : $" ({cloaked} escondidos)")
              + DebounceText.Tooltip(tab?.TrackingSkips.Count ?? 0);

        if (ShieldPopup.IsOpen) FillShieldPopup();
    }

    private void FillShieldPopup()
    {
        var tab = Tabs.Active;
        var host = WebHost(tab?.Address);
        var on = host is null || ShieldOn(host);
        var count = tab?.BlockedCount ?? 0;

        ShieldTitle.Text = host is null ? "Página interna" : on ? "Escudo ativo" : "Escudo desligado";
        ShieldSite.Text = host is null ? "Nada a proteger aqui" : TrackerBlocker.RegistrableDomain(host);
        ShieldToggle.IsEnabled = host is not null;
        ShieldToggle.IsChecked = on;
        StrictToggle.IsEnabled = host is not null && on;
        StrictToggle.IsChecked = host is not null && AppData.IsStrict(host, IsPrivate);
        ShieldHalo.Opacity = on ? 0.9 : 0.25;

        ShieldBigCount.Text = count.ToString();
        ShieldCountLabel.Text = count == 1 ? "rastreador bloqueado nesta página" : "rastreadores bloqueados nesta página";
        ShieldHosts.ItemsSource = tab?.BlockedHosts.Take(6).ToList();

        // Observatório do site: com quem a página falou, agrupado por empresa.
        ShieldObservatoryLine.Text = host is null ? string.Empty
            : "◉  " + SiteObservatory.Summary(SiteObservatory.ByCompany(tab?.Observatory.Snapshot().ThirdParties ?? []));
        ShieldObservatoryLine.Visibility = host is null ? Visibility.Collapsed : Visibility.Visible;
        ShieldObservatoryButton.Visibility = ShieldObservatoryLine.Visibility;
        ShieldCheckHeaders.Text = "✓  Pedido de privacidade (GPC) enviado";
        // Numa aba do motor de vídeo (WebView2) o que vale é o dele, não o do motor principal: as linhas dizem-no.
        var inVideo = tab?.UsesVideoEngine == true;
        ShieldCheckCookies.Text = inVideo ? VideoEngine.ShieldCookiesLine
            : PrivacyPreferences.ThirdPartyCookiesBlocked
            ? "✓  Cookies de terceiros bloqueados" : "✗  Cookies de terceiros permitidos";
        ShieldCheckWebRtc.Text = inVideo ? VideoEngine.ShieldWebRtcLine
            : PrivacyPreferences.WebRtcIpProtected
            ? "✓  WebRTC não expõe o seu IP local" : "✗  WebRTC sem proteção de IP";
        ShieldCheckDns.Text = inVideo ? VideoEngine.ShieldDnsLine
            : PrivacyPreferences.SecureDnsProvider is { } dns
            ? $"✓  DNS cifrado ({dns})" : "·  DNS do sistema (pode ativar DNS cifrado nas definições)";
        ShieldCheckLan.Text = LocalNetworkShieldLine(tab);
        ShieldCheckBounce.Text = DebounceText.ShieldLine(tab?.TrackingSkips ?? [], Data.Settings.Current.SkipTrackingRedirects);
        ShieldCheckCname.Text = CnameUncloak.ShieldLine(tab?.CloakedTrackers ?? [], Data.Settings.Current.UncloakCname, Cname.SourceFor(IsPrivate));
        ShieldCheckBounceSites.Text = BounceText.ShieldLine(IsPrivate ? [] : tab?.BounceSites ?? [], Data.Settings.Current.ForgetBounceTrackers);
        ShieldCheckBounceSites.Visibility = ShieldCheckBounceSites.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var session = ShieldStats.SessionBlocked;
        ShieldSession.Text = $"{session} {(session == 1 ? "rastreador bloqueado" : "rastreadores bloqueados")} desde que abriu o Éteris";
    }

    // Regras genéricas das listas (##.classe, ###id): aplicadas às classes e ids que existem na página,
    // no fim do carregamento e outra vez pouco depois, para anúncios injetados mais tarde. Depois delas, nas duas
    // passagens, a página recolhe os buracos que o Escudo deixou (CosmeticScripts.CollapseHoles): os iframe e img
    // bloqueados desta aba (tab; um painel web não tem) e os lugares só com «Publicidade». Nunca no auto-teste.
    private async Task ApplyGenericCosmeticsAsync(CefSharp.Wpf.HwndHost.ChromiumWebBrowser browser, string url, BrowserTab? tab = null)
    {
        if (WebHost(url) is not { } host || !ShieldOn(host)) return;
        var holes = !SelfTest.IsTestPage(url);

        foreach (var delay in new[] { 0, 2500 })
        {
            if (delay > 0) await Task.Delay(delay);
            if (browser.IsDisposed || browser.GetMainFrame()?.Url != url) return;
            if (ShieldEngine.Cosmetic is { } engine)
            {
                var result = await PageScript.EvaluateAsync(browser, CosmeticScripts.CollectClassesAndIds);
                if (!result.Success || result.Result is not string raw) return;
                var (classes, ids) = CosmeticScripts.ParseClassesAndIds(raw);
                var css = engine.GenericCss(url, classes, ids);
                if (css.Length > 0) PageScript.Run(browser, CosmeticScripts.Inject(css));
            }
            if (holes) PageScript.Run(browser, CosmeticScripts.CollapseHoles(tab?.BlockedElements ?? []));
        }
    }

    // Os bloqueios chegam aos poucos (uma imagem tardia, um iframe de um anúncio que só aparece ao rolar): juntam-se
    // durante um instante e a página recolhe-os de uma vez.
    private readonly HashSet<BrowserTab> _holesQueued = [];

    private async void OnBlockedElement(BrowserTab tab, string url)
    {
        try
        {
            if (!tab.RecordBlockedElement(url) || !_holesQueued.Add(tab)) return;
            try { await Task.Delay(400); }
            finally { _holesQueued.Remove(tab); }
            CollapseHoles(tab);
        }
        catch (Exception) { }
    }

    private void CollapseHoles(BrowserTab tab)
    {
        if (!_browsers.TryGetValue(tab, out var browser) || browser.IsDisposed) return;
        var url = tab.Address;
        if (WebHost(url) is not { } host || !ShieldOn(host) || SelfTest.IsTestPage(url)) return;
        var blocked = tab.BlockedElements;
        if (blocked.Count > 0) PageScript.Run(browser, CosmeticScripts.CollapseHoles(blocked, labels: false));
    }

    private void Shield_Click(object sender, RoutedEventArgs e)
    {
        FillShieldPopup();
        ShieldPopup.IsOpen = !ShieldPopup.IsOpen;
    }

    private void ShieldObservatory_Click(object sender, RoutedEventArgs e) => OpenObservatory();

    private void StrictToggle_Click(object sender, RoutedEventArgs e)
    {
        if (WebHost(Tabs.Active?.Address) is not { } host) return;
        AppData.SetStrict(host, StrictToggle.IsChecked == true, IsPrivate);
        ReloadActive();
        FillShieldPopup();
    }

    private void ShieldToggle_Click(object sender, RoutedEventArgs e)
    {
        if (WebHost(Tabs.Active?.Address) is not { } host) return;
        AppData.SetShield(host, ShieldToggle.IsChecked == true, IsPrivate);
        ReloadActive();
        UpdateShieldButton();
        FillShieldPopup();
    }
}
