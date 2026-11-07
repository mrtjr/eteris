using System.Windows;
using Eteris.Browser.Permissions;
using Eteris.Browser.Shields;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// Permissão para a rede local (Shields/LocalNetwork.cs): os pedidos de uma página pública para este computador ou para
// a rede de casa são travados e aparecem no Escudo da aba.
public partial class MainWindow
{
    // «Sempre» fica nas permissões do site; «só desta vez» em memória (à parte na janela privada).
    private bool IsLocalNetworkAllowed(string site)
        => LocalNetworkGrants.IsAllowed(site, IsPrivate) || SitePermissions.Get(site, SitePermission.LocalNetwork) == true;

    // Chega na thread do CEF.
    private void OnLocalNetworkBlocked(BrowserTab tab, string site, string url) => Dispatcher.InvokeAsync(() =>
    {
        tab.RecordLocalNetworkBlocked(LocalNetwork.Label(url));
        if (ReferenceEquals(tab, Tabs.Active)) UpdateShieldButton();
    });

    private string LocalNetworkShieldLine(BrowserTab? tab)
        => LocalNetworkText.ShieldLine(tab?.LocalNetworkBlocked ?? []);
}
