using CefSharp;
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

    // Sites a quem o cartão já perguntou nesta janela (um «Bloquear» não volta a incomodar).
    private readonly HashSet<string> _localNetworkAsked = new(StringComparer.OrdinalIgnoreCase);

    // Chega na thread do CEF.
    private void OnLocalNetworkBlocked(BrowserTab tab, string site, string url) => Dispatcher.InvokeAsync(() =>
    {
        var label = LocalNetwork.Label(url);
        tab.RecordLocalNetworkBlocked(label);
        if (ReferenceEquals(tab, Tabs.Active)) UpdateShieldButton();
        // Só para a página à vista: uma aba em segundo plano fica registada no Escudo sem interromper.
        if (!ReferenceEquals(tab, Tabs.Active) && _split?.PaneOf(tab) is null) return;
        if (!_localNetworkAsked.Add(site)) return;
        _permissionQueue.Enqueue(new PendingPermission(site, [SitePermission.LocalNetwork], allowed =>
        {
            // Os pedidos travados já falharam: a página volta a carregar para os repetir com autorização.
            if (allowed && HasEngine(tab) && _browsers.TryGetValue(tab, out var browser)) browser.Reload();
        }, LocalNetworkText.CardDetail(label)));
        ShowNextPermission();
    });

    private string LocalNetworkShieldLine(BrowserTab? tab)
        => LocalNetworkText.ShieldLine(tab?.LocalNetworkBlocked ?? []);
}
