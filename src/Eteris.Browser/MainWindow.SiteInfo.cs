using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CefSharp;
using Eteris.Browser.Browser;
using Eteris.Browser.Permissions;

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

        var isWeb = info.Kind is ConnectionKind.Secure or ConnectionKind.Insecure or ConnectionKind.Local;
        SiteInfoSiteSection.Visibility = isWeb ? Visibility.Visible : Visibility.Collapsed;
        if (!isWeb) return;

        var rows = SitePermissions.ForSite(PermissionRules.Site(info.Origin))
            .Select(p => new PermissionRow(p.Kind, Permissions.PermissionText.Name(p.Kind),
                p.Allowed ? "Permitido" : "Bloqueado", p.Allowed ? AllowedBrush : BlockedBrush))
            .ToList();
        SiteInfoPermissions.ItemsSource = rows;
        SiteInfoNoPermissions.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var count = tab?.BlockedCount ?? 0;
        SiteInfoTrackers.Text = count switch
        {
            0 => "Nenhum rastreador encontrado nesta página.",
            1 => "1 rastreador bloqueado nesta página pelo escudo.",
            _ => $"{count} rastreadores bloqueados nesta página pelo escudo.",
        };
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
