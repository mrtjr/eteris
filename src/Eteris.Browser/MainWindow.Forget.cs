using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using CefSharp;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// «Esquecer este site ao fechar» e «Triturar»: cookies, armazenamento (local, IndexedDB, cache e trabalhadores),
// histórico e permissões de um site — o domínio registável e os seus subdomínios, nunca outro (ver SiteScope).
// A cache HTTP do motor não se separa por site (não há API para isso): fica, como no Brave.
public partial class MainWindow
{
    // Um temporizador por site, partilhado pelas janelas: fechar a última aba agenda o apagar; reabrir uma aba do
    // site nesses segundos (Ctrl+Z, Ctrl+Shift+T) cancela-o.
    private static readonly Dictionary<string, DispatcherTimer> s_forgetTimers = new(StringComparer.Ordinal);
    private static readonly TimeSpan ForgetDelay = TimeSpan.FromSeconds(10);

    public sealed record ShredResult(int Cookies, int History);

    private void WatchForgetOnClose()
    {
        // A janela privada não grava nada: não há o que esquecer depois.
        if (IsPrivate) return;
        Tabs.Tabs.CollectionChanged += (_, e) =>
        {
            if (e.Action is not (NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace) || e.OldItems is null) return;
            foreach (var tab in e.OldItems.OfType<BrowserTab>())
                if (SiteScope.SiteOf(tab.Address) is { } site && AppData.IsForgetOnClose(site)) ScheduleForget(site);
        };
    }

    private static void ScheduleForget(string site)
    {
        if (s_forgetTimers.TryGetValue(site, out var running)) running.Stop();
        var timer = new DispatcherTimer { Interval = ForgetDelay };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            s_forgetTimers.Remove(site);
            if (SiteScope.StillOpen(site, OpenAddresses()) || !AppData.IsForgetOnClose(site)) return;
            if (NormalWindows().FirstOrDefault() is not { } window) return;
            var result = await window.ShredSiteAsync(site);
            window.ShowToast($"{site} foi esquecido — {Describe(result)}");
        };
        s_forgetTimers[site] = timer;
        timer.Start();
    }

    private static IEnumerable<MainWindow> NormalWindows()
        => Application.Current?.Windows.OfType<MainWindow>().Where(w => !w.IsPrivate) ?? [];

    // Todas as abas das janelas normais: a faixa e a reserva dos outros Espaços (a dormir contam), e os sites fixados
    // na barra lateral (um site fixado está sempre «aberto»: esquecê-lo tirava-lhe a sessão a cada fecho de aba).
    private static IEnumerable<string> OpenAddresses()
        => NormalWindows().SelectMany(w => w.Tabs.Tabs.Select(t => t.Address)
            .Concat(w.Tabs.Workspaces.Where(s => !s.IsCurrent).SelectMany(s => s.StoredTabs.Select(t => t.Address)))
            .Concat(w._webPanels.Select(p => p.Url)));

    private static string Describe(ShredResult result)
        => $"{(result.Cookies == 1 ? "1 cookie" : $"{result.Cookies} cookies")} e "
           + $"{(result.History == 1 ? "1 entrada do histórico" : $"{result.History} entradas do histórico")} apagados";

    private IRequestContext CookieContext => PrivateContext ?? Cef.GetGlobalRequestContext();

    private async Task<int> CountSiteCookiesAsync(string site)
    {
        try
        {
            var cookies = await CookieContext.GetCookieManager(null).VisitAllCookiesAsync();
            return cookies.Count(c => SiteScope.CookieBelongs(c.Domain, site));
        }
        catch (Exception) { return 0; }
    }

    // Apaga já tudo o que o site deixou (cookies e armazenamento do contexto desta janela; histórico e permissões
    // do perfil, fora da janela privada). Só toca no site indicado.
    private async Task<ShredResult> ShredSiteAsync(string site)
    {
        var history = 0;
        var seen = Tabs.Tabs.Select(t => t.Address).ToList();
        if (!IsPrivate)
        {
            try
            {
                history = AppData.History.CountSite(site);
                seen.AddRange(AppData.History.SiteUrls(site));
                AppData.History.DeleteSite(site);
            }
            catch (Exception) { }
            AppData.ForgetSitePermissions(site);
            Bounces.Forget(site);
        }
        ForgetSiteInUndo(site);
        var cookies = await ClearSiteStorageAsync(site, seen);
        return new ShredResult(cookies, history);
    }

    // Cookies e armazenamento (local, IndexedDB, cache e trabalhadores) do site e dos seus subdomínios, no contexto
    // desta janela; devolve quantos cookies apagou. Usado por «Triturar», «Esquecer ao fechar» e pelos sites de salto.
    private async Task<int> ClearSiteStorageAsync(string site, IEnumerable<string> seenUrls)
    {
        var cookies = 0;
        try
        {
            var manager = CookieContext.GetCookieManager(null);
            foreach (var cookie in (await manager.VisitAllCookiesAsync()).Where(c => SiteScope.CookieBelongs(c.Domain, site)))
                if (await manager.DeleteCookiesAsync(SiteScope.CookieUrl(cookie.Domain, cookie.Path), cookie.Name) > 0) cookies++;
        }
        catch (Exception) { }

        // O armazenamento apaga-se por origem, pelo DevTools de uma página viva deste contexto.
        if (_browsers.Values.FirstOrDefault(b => b.IsBrowserInitialized) is { } browser)
        {
            try
            {
                using var client = browser.GetDevToolsClient();
                foreach (var origin in SiteScope.Origins(site, seenUrls))
                    await client.Storage.ClearDataForOriginAsync(origin, "all");
            }
            catch (Exception) { }
        }
        return cookies;
    }

    // ---------- Painel do cadeado ----------

    private string? _shredSite;

    private void FillForgetSection(SiteInfoResult info)
    {
        SiteInfoShredConfirm.Visibility = Visibility.Collapsed;
        var site = SiteScope.SiteOf(Tabs.Active?.Address);
        SiteInfoShred.Visibility = site is null ? Visibility.Collapsed : Visibility.Visible;
        // Na janela privada tudo é esquecido ao fechar: o interruptor não tem sentido lá.
        SiteInfoForgetRow.Visibility = site is null || IsPrivate ? Visibility.Collapsed : Visibility.Visible;
        if (site is null) return;
        SiteInfoForgetToggle.IsChecked = AppData.IsForgetOnClose(site);
        SiteInfoForgetHint.Text = $"Ao fechar a última aba de {site}, apagam-se os cookies, o armazenamento, o histórico e as permissões.";
    }

    private void SiteInfoForgetToggle_Click(object sender, RoutedEventArgs e)
    {
        if (IsPrivate || SiteScope.SiteOf(Tabs.Active?.Address) is not { } site) return;
        var on = SiteInfoForgetToggle.IsChecked == true;
        AppData.SetForgetOnClose(site, on);
        ShowToast(on ? $"{site} será esquecido ao fechar a última aba" : $"{site} deixa de ser esquecido ao fechar");
    }

    // Primeiro mostra o que vai apagar (contado agora); só o segundo botão apaga.
    private async void SiteInfoShred_Click(object sender, RoutedEventArgs e)
    {
        if (SiteScope.SiteOf(Tabs.Active?.Address) is not { } site) return;
        _shredSite = site;
        var cookies = await CountSiteCookiesAsync(site);
        var history = 0;
        if (!IsPrivate)
        {
            try { history = AppData.History.CountSite(site); }
            catch (Exception) { }
        }
        if (_shredSite != site) return;
        SiteInfoShredText.Text = SiteScope.ShredSummary(site, cookies, history);
        SiteInfoShredConfirm.Visibility = Visibility.Visible;
    }

    private void SiteInfoShredCancel_Click(object sender, RoutedEventArgs e)
    {
        _shredSite = null;
        SiteInfoShredConfirm.Visibility = Visibility.Collapsed;
    }

    private async void SiteInfoShredConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (_shredSite is not { } site) return;
        _shredSite = null;
        SiteInfoPopup.IsOpen = false;
        // As abas do site nesta janela fecham primeiro (senão voltavam a escrever cookies logo a seguir), sem irem
        // para as fechadas recentes nem para a pilha de anulação: triturar não se anula.
        foreach (var tab in Tabs.Tabs.Where(t => SiteScope.Belongs(t.Address, site)).ToList()) Tabs.CloseQuietly(tab);
        if (Tabs.Tabs.Count == 0) Tabs.NewTab(NewTabUrl);
        var result = await ShredSiteAsync(site);
        ShowToast($"{site} triturado — {Describe(result)}");
    }
}
