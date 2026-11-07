using System.Windows;
using System.Windows.Media;
using CefSharp;
using Eteris.Browser.Data;
using Eteris.Browser.Shields;
using Eteris.Browser.Browser;

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
    private static string SiteCosmeticScript(string url)
    {
        if (WebHost(url) is not { } host || !AppData.IsShieldOn(host) || ShieldEngine.Cosmetic is not { } engine) return string.Empty;
        var script = CosmeticScripts.InjectEarly(engine.UrlCss(url));
        return script.Length == 0 ? string.Empty : script + "\n;\n";
    }

    private void UpdateShieldButton()
    {
        var tab = Tabs.Active;
        var host = WebHost(tab?.Address);
        var on = host is null || AppData.IsShieldOn(host);
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
        var on = host is null || AppData.IsShieldOn(host);
        var count = tab?.BlockedCount ?? 0;

        ShieldTitle.Text = host is null ? "Página interna" : on ? "Escudo ativo" : "Escudo desligado";
        ShieldSite.Text = host is null ? "Nada a proteger aqui" : TrackerBlocker.RegistrableDomain(host);
        ShieldToggle.IsEnabled = host is not null;
        ShieldToggle.IsChecked = on;
        StrictToggle.IsEnabled = host is not null && on;
        StrictToggle.IsChecked = host is not null && AppData.IsStrict(host);
        ShieldHalo.Opacity = on ? 0.9 : 0.25;

        ShieldBigCount.Text = count.ToString();
        ShieldCountLabel.Text = count == 1 ? "rastreador bloqueado nesta página" : "rastreadores bloqueados nesta página";
        ShieldHosts.ItemsSource = tab?.BlockedHosts.Take(6).ToList();

        ShieldCheckHeaders.Text = "✓  Pedido «Não rastrear» e GPC enviados";
        ShieldCheckCookies.Text = PrivacyPreferences.ThirdPartyCookiesBlocked
            ? "✓  Cookies de terceiros bloqueados" : "✗  Cookies de terceiros permitidos";
        ShieldCheckWebRtc.Text = PrivacyPreferences.WebRtcIpProtected
            ? "✓  WebRTC não expõe o seu IP local" : "✗  WebRTC sem proteção de IP";
        ShieldCheckDns.Text = PrivacyPreferences.SecureDnsProvider is { } dns
            ? $"✓  DNS cifrado ({dns})" : "·  DNS do sistema (pode ativar DNS cifrado nas definições)";
        ShieldCheckLan.Text = LocalNetworkShieldLine(tab);
        ShieldCheckBounce.Text = DebounceText.ShieldLine(tab?.TrackingSkips ?? [], Data.Settings.Current.SkipTrackingRedirects);
        ShieldCheckCname.Text = CnameUncloak.ShieldLine(tab?.CloakedTrackers ?? [], Data.Settings.Current.UncloakCname);
        ShieldCheckBounceSites.Text = BounceText.ShieldLine(IsPrivate ? [] : tab?.BounceSites ?? [], Data.Settings.Current.ForgetBounceTrackers);
        ShieldCheckBounceSites.Visibility = ShieldCheckBounceSites.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var session = ShieldStats.SessionBlocked;
        ShieldSession.Text = $"{session} {(session == 1 ? "rastreador bloqueado" : "rastreadores bloqueados")} desde que abriu o Éteris";
    }

    // Regras genéricas das listas (##.classe, ###id): aplicadas às classes e ids que existem na página,
    // no fim do carregamento e outra vez pouco depois, para anúncios injetados mais tarde.
    private static async Task ApplyGenericCosmeticsAsync(CefSharp.Wpf.HwndHost.ChromiumWebBrowser browser, string url)
    {
        if (ShieldEngine.Cosmetic is not { } engine) return;
        if (WebHost(url) is not { } host || !AppData.IsShieldOn(host)) return;

        foreach (var delay in new[] { 0, 2500 })
        {
            if (delay > 0) await Task.Delay(delay);
            if (browser.IsDisposed || browser.GetMainFrame()?.Url != url) return;
            var result = await PageScript.EvaluateAsync(browser, CosmeticScripts.CollectClassesAndIds);
            if (!result.Success || result.Result is not string raw) return;
            var (classes, ids) = CosmeticScripts.ParseClassesAndIds(raw);
            var css = engine.GenericCss(url, classes, ids);
            if (css.Length > 0) PageScript.Run(browser, CosmeticScripts.Inject(css));
        }
    }

    private void Shield_Click(object sender, RoutedEventArgs e)
    {
        FillShieldPopup();
        ShieldPopup.IsOpen = !ShieldPopup.IsOpen;
    }

    private void StrictToggle_Click(object sender, RoutedEventArgs e)
    {
        if (WebHost(Tabs.Active?.Address) is not { } host) return;
        AppData.SetStrict(host, StrictToggle.IsChecked == true);
        ReloadActive();
        FillShieldPopup();
    }

    private void ShieldToggle_Click(object sender, RoutedEventArgs e)
    {
        if (WebHost(Tabs.Active?.Address) is not { } host) return;
        AppData.SetShield(host, ShieldToggle.IsChecked == true);
        ReloadActive();
        UpdateShieldButton();
        FillShieldPopup();
    }
}
