using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CefSharp;
using Eteris.Browser.Browser;
using Eteris.Browser.Permissions;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

public partial class MainWindow
{
    private sealed record PermissionRow(SitePermission Kind, string Name, string State, Brush StateBrush);

    private static readonly Brush AllowedBrush = Frozen("#3DDC97");
    private static readonly Brush BlockedBrush = Frozen("#FF7A90");
    private static readonly Brush WarningBrush = Frozen("#FFB547");
    private static readonly Brush NeutralBrush = Frozen("#8E95B3");

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private void SiteInfo_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.Active is null) return;
        FillSiteInfo();
        SiteInfoPopup.IsOpen = !SiteInfoPopup.IsOpen;
    }

    private void FillSiteInfo()
    {
        var tab = Tabs.Active;
        var info = SiteInfo.Describe(tab?.Address ?? string.Empty);
        SiteInfoTitle.Text = info.Title;
        SiteInfoHost.Text = info.Host;
        SiteInfoDetail.Text = info.Detail;
        (SiteInfoHalo.Fill, SiteInfoGlyph.Text) = info.Kind switch
        {
            ConnectionKind.Secure => (AllowedBrush, ""),
            ConnectionKind.Insecure => (WarningBrush, ""),
            ConnectionKind.Internal => ((Brush)FindResource("IrisBrush"), ""),
            _ => (NeutralBrush, ""),
        };

        SiteInfoRetryHttps.Visibility = info.Kind == ConnectionKind.Insecure && Uri.TryCreate(tab?.Address, UriKind.Absolute, out var page)
                                         && Shields.ShieldEngine.Https.IsAllowed(page.Host, IsPrivate)
            ? Visibility.Visible : Visibility.Collapsed;

        var isWeb = info.Kind is ConnectionKind.Secure or ConnectionKind.Insecure or ConnectionKind.Local;
        SiteInfoSiteSection.Visibility = isWeb ? Visibility.Visible : Visibility.Collapsed;
        if (!isWeb) return;

        var rows = SitePermissions.ForSite(PermissionRules.Site(info.Origin))
            .Select(p => new PermissionRow(p.Kind, Permissions.PermissionText.Name(p.Kind),
                p.Allowed ? "Permitido" : "Bloqueado", p.Allowed ? AllowedBrush : BlockedBrush))
            .ToList();
        SiteInfoPermissions.ItemsSource = rows;
        SiteInfoNoPermissions.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        FillForgetSection(info);

        var count = tab?.BlockedCount ?? 0;
        SiteInfoTrackers.Text = count switch
        {
            0 => "Nenhum rastreador encontrado nesta página.",
            1 => "1 rastreador bloqueado nesta página pelo escudo.",
            _ => $"{count} rastreadores bloqueados nesta página pelo escudo.",
        };
        if (tab is not null) _ = FillSiteKnowledgeAsync(tab, info);
    }

    // «O que este site sabe de ti»: primeiro o que já se sabe sem perguntar ao motor, depois cookies e armazenamento.
    private async Task FillSiteKnowledgeAsync(BrowserTab tab, SiteInfoResult info)
    {
        var snap = tab.Observatory.Snapshot();
        var groups = Shields.SiteObservatory.ByCompany(snap.ThirdParties);
        var referrer = Shields.ReferrerCap.Decide(tab.Address, "https://outro-site.example/") is { Kind: not Shields.ReferrerCap.Kind.Strip } cap
            ? cap.Value ?? info.Origin + "/" : null;
        SiteKnowledge.Facts Facts(IReadOnlyList<DateTime?> cookies, long? storage) => new(cookies, storage, snap.ThirdParties.Count,
            groups.Count(g => g.Known), snap.ThirdParties.Count(d => d.Blocked > 0), referrer, tab.LocalNetworkBlocked.Sum(p => p.Value), DateTime.Now);
        SiteKnowledgeList.ItemsSource = SiteKnowledge.Describe(Facts([], null)).Skip(2).ToList();

        var site = Data.SiteScope.SiteOf(tab.Address);
        IReadOnlyList<DateTime?> expiries = [];
        long? storage = null;
        try
        {
            if (site is not null)
                expiries = (await CookieContext.GetCookieManager(null).VisitAllCookiesAsync())
                    .Where(c => Data.SiteScope.CookieBelongs(c.Domain, site)).Select(c => c.Expires).ToList();
        }
        catch (Exception) { }
        try
        {
            if (ActiveBrowser is { } browser && ReferenceEquals(Tabs.Active, tab))
            {
                using var client = browser.GetDevToolsClient();
                var call = client.Storage.GetUsageAndQuotaAsync(info.Origin);
                if (await Task.WhenAny(call, Task.Delay(1500)) == call) storage = (long)(await call).Usage;
            }
        }
        catch (Exception) { }
        if (!SiteInfoPopup.IsOpen || !ReferenceEquals(Tabs.Active, tab)) return;
        SiteKnowledgeList.ItemsSource = SiteKnowledge.Describe(Facts(expiries, storage));
    }

    // Desfaz «Continuar sem segurança» e «Sempre para este site»: o site volta a ser aberto por https:// primeiro (e,
    // se continuar sem HTTPS, volta o aviso).
    private void SiteInfoRetryHttps_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.Active is not { } tab || !Uri.TryCreate(tab.Address, UriKind.Absolute, out var page)) return;
        SiteInfoPopup.IsOpen = false;
        Data.AppData.SetHttpException(page.Host, false);
        Shields.ShieldEngine.Https.Forget(page.Host);
        ShowToast($"{page.Host} volta a abrir por HTTPS primeiro");
        ReloadActive();
    }

    private void SiteInfoObservatory_Click(object sender, RoutedEventArgs e)
    {
        SiteInfoPopup.IsOpen = false;
        OpenObservatory();
    }

    private void SiteInfoForget_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SitePermission kind } || Tabs.Active is not { } tab) return;
        SitePermissions.Forget(PermissionRules.Site(SiteInfo.Describe(tab.Address).Origin), kind);
        FillSiteInfo();
        ShowToast($"{Permissions.PermissionText.Name(kind)}: o site voltará a perguntar");
    }

    // Apaga cookies (do contexto desta janela) e armazenamento local da origem; depois recarrega.
    private async void SiteInfoClear_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.Active is not { } tab || ActiveBrowser is not { } browser) return;
        var info = SiteInfo.Describe(tab.Address);
        SiteInfoPopup.IsOpen = false;
        try
        {
            var context = PrivateContext ?? Cef.GetGlobalRequestContext();
            await context.GetCookieManager(null).DeleteCookiesAsync(info.Origin + "/");
            using var client = browser.GetDevToolsClient();
            await client.Storage.ClearDataForOriginAsync(info.Origin, "all");
            ShowToast($"Dados de {info.Host} apagados");
            ReloadActive();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            ShowToast("Não foi possível apagar os dados deste site");
        }
    }
}
