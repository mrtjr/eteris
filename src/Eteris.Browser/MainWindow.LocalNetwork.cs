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

    // Sites a quem o cartão já perguntou nesta janela (um «Bloquear» não volta a incomodar até recarregar a página).
    private readonly HashSet<string> _localNetworkAsked = new(StringComparer.OrdinalIgnoreCase);

    // Pedidos travados à espera da thread da janela: uma página que varre a rede local (milhares de pedidos) faz um só
    // despacho por lote, não um por pedido.
    private readonly System.Collections.Concurrent.ConcurrentQueue<(BrowserTab Tab, string Site, string Url)> _localNetworkReports = new();
    private int _localNetworkDraining;

    // Chega na thread do CEF.
    private void OnLocalNetworkBlocked(BrowserTab tab, string site, string url)
    {
        _localNetworkReports.Enqueue((tab, site, url));
        if (Interlocked.Exchange(ref _localNetworkDraining, 1) == 0) Dispatcher.InvokeAsync(DrainLocalNetworkReports);
    }

    private void DrainLocalNetworkReports()
    {
        Volatile.Write(ref _localNetworkDraining, 0);
        var active = false;
        while (_localNetworkReports.TryDequeue(out var report))
        {
            var (tab, site, url) = report;
            var label = LocalNetwork.Label(url);
            tab.RecordLocalNetworkBlocked(label);
            active |= ReferenceEquals(tab, Tabs.Active);
            // O auto-teste de privacidade tenta de propósito chegar a 127.0.0.1: fica registado, sem perguntar nada.
            if (Browser.SelfTest.IsTestHost(Uri.TryCreate(site, UriKind.Absolute, out var origin) ? origin.Host : site)) continue;
            // Só para a página à vista: uma aba em segundo plano fica registada no Escudo sem interromper.
            if (!ReferenceEquals(tab, Tabs.Active) && _split?.PaneOf(tab) is null) continue;
            if (!_localNetworkAsked.Add(site)) continue;
            // O cartão que sai sem resposta (a aba navegou, fechou ou adormeceu) deixa o site voltar a perguntar (Drop).
            _pendingPermissions.Add(new PendingPermission(site, [SitePermission.LocalNetwork], allowed =>
            {
                // Os pedidos travados já falharam: a página volta a carregar para os repetir com autorização.
                if (allowed && HasEngine(tab) && _browsers.TryGetValue(tab, out var browser) && IsReady(browser)) browser.Reload();
            }, Drop: () => _localNetworkAsked.Remove(site), EmbeddedIn: null, CanRemember: false, Detail: LocalNetworkText.CardDetail(label), Tab: tab));
            ShowNextPermission();
        }
        if (active) UpdateShieldButton();
    }

    private string LocalNetworkShieldLine(BrowserTab? tab)
        => LocalNetworkText.ShieldLine(tab?.LocalNetworkBlocked ?? []);
}
