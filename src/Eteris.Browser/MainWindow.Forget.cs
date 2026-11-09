using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CefSharp;
using CefSharp.Wpf.HwndHost;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// «Esquecer este site ao fechar» e «Triturar»: cookies, armazenamento (local, IndexedDB, cache e trabalhadores),
// histórico e permissões de um site — o domínio registável e os seus subdomínios, nunca outro (ver SiteScope).
// A cache HTTP do motor não se separa por site (não há API para isso): fica, como no Brave.
public partial class MainWindow
{
    // Um temporizador por site, partilhado pelas janelas: deixar de ter abas do site agenda o apagar; reabrir uma aba
    // do site nesses segundos (Ctrl+Z, Ctrl+Shift+T) cancela-o.
    private static readonly Dictionary<string, DispatcherTimer> s_forgetTimers = new(StringComparer.Ordinal);
    private static readonly TimeSpan ForgetDelay = TimeSpan.FromSeconds(10);
    // Ao fechar a última janela, o apagar tem este tempo antes de a janela fechar na mesma.
    private static readonly TimeSpan ForgetOnExitLimit = TimeSpan.FromSeconds(6);
    private static bool s_forgetSweepDone;
    private bool _forgetOnCloseDone;

    // Storage: o armazenamento foi mesmo apagado (pelo DevTools de uma página viva deste contexto).
    public sealed record ShredResult(int Cookies, int History, bool Storage);

    private void WatchForgetOnClose()
    {
        // A janela privada não grava nada: não há o que esquecer depois.
        if (IsPrivate) return;
        Tabs.Tabs.CollectionChanged += (_, e) =>
        {
            if (e.Action is not (NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace) || e.OldItems is null) return;
            ScheduleForgetFor(e.OldItems.OfType<BrowserTab>().Select(t => t.Address));
        };
        Closing += ForgetWhenClosing;
        // No arranque, o que ficou por apagar (o Éteris saiu sem tempo para isso: fim de sessão do Windows, falha):
        // os sites com «esquecer» que não voltaram na sessão reposta.
        if (s_forgetSweepDone) return;
        s_forgetSweepDone = true;
        ContentRendered += async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            await ForgetLeftoversAsync();
        };
    }

    // Abas que saíram (fechadas, Espaço apagado): cada site com «esquecer» é verificado daqui a uns segundos (o
    // temporizador só apaga se já não houver nenhuma aba dele).
    private static void ScheduleForgetFor(IEnumerable<string?> addresses)
    {
        foreach (var site in SiteScope.SitesLeaving(addresses, [], AppData.IsForgetOnClose)) ScheduleForget(site);
    }

    // A aba foi de um site para outro: o anterior pode ter ficado sem abas.
    private void NoteSiteLeft(string? from, string to)
    {
        if (IsPrivate || SiteScope.SiteOf(from) is not { } left || left == SiteScope.SiteOf(to)) return;
        if (AppData.IsForgetOnClose(left)) ScheduleForget(left);
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
            if (NormalWindows().FirstOrDefault(w => !w._forgetOnCloseDone) is not { } window) return;
            var result = await window.ShredSiteAsync(site);
            window.ShowToast($"{site} foi esquecido — {SiteScope.ShredDone(result.Cookies, result.History, result.Storage)}");
        };
        s_forgetTimers[site] = timer;
        timer.Start();
    }

    // Fechar uma janela normal tira-lhe as abas sem passar pela faixa. Com outras janelas abertas, os sites que ficam
    // sem abas seguem o caminho normal (temporizador). A última janela apaga já — os sites que deixa e os que estavam à
    // espera do temporizador —, menos o que fica na sessão para reabrir («Continuar onde parei»).
    private void ForgetWhenClosing(object? sender, CancelEventArgs e)
    {
        if (e.Cancel || _forgetOnCloseDone || Updates.UpdateService.RestartRequested) return;
        var mine = AddressesOf(this).ToList();
        var others = NormalWindows().Where(w => !ReferenceEquals(w, this) && !w._forgetOnCloseDone).ToList();
        if (others.Count > 0)
        {
            foreach (var site in SiteScope.SitesLeaving(mine, others.SelectMany(AddressesOf), AppData.IsForgetOnClose)) ScheduleForget(site);
            return;
        }
        IEnumerable<string> staying = _ownsSession && Data.Settings.Current.Startup == "restore" ? mine : [];
        var sites = SiteScope.SitesLeaving(mine.Concat(s_forgetTimers.Keys.Select(site => "https://" + site + "/")), staying, AppData.IsForgetOnClose);
        if (sites.Count == 0) return;
        foreach (var timer in s_forgetTimers.Values) timer.Stop();
        s_forgetTimers.Clear();
        // Os motores desta janela ainda estão vivos: apaga-se com eles e só depois a janela fecha (sem voltar a
        // perguntar pelas abas trancadas, que já foram confirmadas).
        e.Cancel = true;
        _forgetOnCloseDone = true;
        Dispatcher.InvokeAsync(async () =>
        {
            try { await Task.WhenAny(ForgetSitesAsync(sites), Task.Delay(ForgetOnExitLimit)); }
            finally
            {
                _lockedCloseConfirmUntil = DateTime.MaxValue;
                Close();
            }
        });
    }

    private async Task ForgetSitesAsync(IReadOnlyList<string> sites)
    {
        foreach (var site in sites)
        {
            try { await ShredSiteAsync(site); }
            catch (Exception) { }
        }
    }

    private async Task ForgetLeftoversAsync()
    {
        var open = OpenAddresses().ToList();
        var forgotten = new List<string>();
        foreach (var site in AppData.ForgetOnCloseSites().Where(s => !SiteScope.StillOpen(s, open)).ToList())
        {
            ShredResult result;
            try { result = await ShredSiteAsync(site); }
            catch (Exception) { continue; }
            if (result.Cookies + result.History > 0) forgotten.Add(site);
        }
        if (forgotten.Count > 0)
            ShowToast(forgotten.Count == 1 ? $"{forgotten[0]} foi esquecido (tinha ficado por apagar ao sair)"
                : $"{forgotten.Count} sites esquecidos (tinham ficado por apagar ao sair): {string.Join(", ", forgotten)}");
    }

    private static IEnumerable<MainWindow> NormalWindows()
        => Application.Current?.Windows.OfType<MainWindow>().Where(w => !w.IsPrivate) ?? [];

    // Todas as abas das janelas normais: a faixa e a reserva dos outros Espaços (a dormir contam), e os sites fixados
    // na barra lateral (um site fixado está sempre «aberto»: esquecê-lo tirava-lhe a sessão a cada fecho de aba).
    // Uma janela a fechar já não conta.
    private static IEnumerable<string> OpenAddresses() => NormalWindows().Where(w => !w._forgetOnCloseDone).SelectMany(AddressesOf);

    private static IEnumerable<string> AddressesOf(MainWindow window)
        => window.Tabs.Tabs.Select(t => t.Address)
            .Concat(window.Tabs.Workspaces.Where(s => !s.IsCurrent).SelectMany(s => s.StoredTabs.Select(t => t.Address)))
            .Concat(window._webPanels.Select(p => p.Url));

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

    // Apaga já tudo o que o site deixou (cookies e armazenamento do contexto desta janela; histórico e permissões do
    // perfil, ou as desta janela na privada). Só toca no site indicado. seenBefore: endereços do site recolhidos antes
    // de fechar as abas dele (cada origem tem o seu armazenamento).
    private async Task<ShredResult> ShredSiteAsync(string site, IEnumerable<string>? seenBefore = null)
    {
        var history = 0;
        var seen = Tabs.Tabs.Select(t => t.Address).Concat(seenBefore ?? []).ToList();
        if (!IsPrivate)
        {
            try
            {
                // Fora da thread da janela: o histórico pode ter centenas de milhares de linhas.
                var taken = await Task.Run(() =>
                {
                    AppData.FlushHistory();
                    return AppData.History.TakeSite(site);
                });
                history = taken.Count;
                seen.AddRange(taken.Urls);
            }
            catch (Exception) { }
            AppData.ForgetSitePermissions(site);
            Bounces.Forget(site);
        }
        else
        {
            try { SitePermissions.ForgetWhere(key => SiteScope.HostKeyBelongs(key, site)); }
            catch (Exception) { }
        }
        Shields.LocalNetworkGrants.ForgetWhere(key => SiteScope.HostKeyBelongs(key, site), IsPrivate);
        ForgetSiteInUndo(site);
        var (cookies, storage) = await ClearSiteStorageAsync(site, seen);
        return new ShredResult(cookies, history, storage);
    }

    // Cookies e armazenamento (local, IndexedDB, cache e trabalhadores) do site e dos seus subdomínios, no contexto
    // desta janela; devolve quantos cookies apagou e se o armazenamento foi apagado. Usado por «Triturar», «Esquecer
    // ao fechar» e pelos sites de salto. Também tira ao motor as decisões dele sobre a rede local dessas origens.
    private async Task<(int Cookies, bool Storage)> ClearSiteStorageAsync(string site, IEnumerable<string> seenUrls)
    {
        var cookies = 0;
        var seen = seenUrls.ToList();
        try
        {
            var manager = CookieContext.GetCookieManager(null);
            foreach (var cookie in (await manager.VisitAllCookiesAsync()).Where(c => SiteScope.CookieBelongs(c.Domain, site)))
            {
                // Um subdomínio com cookies (accounts.exemplo.pt num iframe) também tem armazenamento.
                var host = cookie.Domain.Trim().TrimStart('.');
                seen.Add("https://" + host + "/");
                seen.Add("http://" + host + "/");
                if (await manager.DeleteCookiesAsync(SiteScope.CookieUrl(cookie.Domain, cookie.Path), cookie.Name) > 0) cookies++;
            }
        }
        catch (Exception) { }

        var origins = SiteScope.Origins(site, seen);
        foreach (var origin in origins) Permissions.EngineLocalNetwork.Reset(CookieContext, origin);

        // O armazenamento apaga-se por origem, pelo DevTools de uma página viva deste contexto. Logo depois de fechar as
        // abas do site, a aba que fica pode ainda não ter motor: espera-se por ele.
        if (await LiveBrowserAsync(TimeSpan.FromSeconds(8)) is not { } browser) return (cookies, false);
        try
        {
            using var client = browser.GetDevToolsClient();
            foreach (var origin in origins)
            {
                // Uma origem recusada pelo motor não impede as outras.
                try { await client.Storage.ClearDataForOriginAsync(origin, "all"); }
                catch (Exception) { }
            }
            return (cookies, true);
        }
        catch (Exception) { return (cookies, false); }
    }

    private async Task<ChromiumWebBrowser?> LiveBrowserAsync(TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (true)
        {
            if (_browsers.Values.FirstOrDefault(b => !b.IsDisposed && b.IsBrowserInitialized) is { } browser) return browser;
            if (DateTime.UtcNow >= until) return null;
            await Task.Delay(100);
        }
    }

    // ---------- Painel do cadeado ----------

    private string? _shredSite;

    private void FillForgetSection(SiteInfoResult info)
    {
        _shredSite = null;
        SiteInfoShredConfirm.Visibility = Visibility.Collapsed;
        var site = SiteScope.SiteOf(Tabs.Active?.Address);
        SiteInfoShred.Visibility = site is null ? Visibility.Collapsed : Visibility.Visible;
        // Na janela privada tudo é esquecido ao fechar: o interruptor não tem sentido lá.
        SiteInfoForgetRow.Visibility = site is null || IsPrivate ? Visibility.Collapsed : Visibility.Visible;
        if (site is null) return;
        SiteInfoForgetToggle.IsChecked = AppData.IsForgetOnClose(site);
        SiteInfoForgetHint.Text = $"Quando deixar de ter abas de {site} (fechá-las, ir para outro site ou sair do Éteris), apagam-se os cookies, o armazenamento, o histórico e as permissões. As palavras-passe guardadas ficam.";
    }

    private void SiteInfoForgetToggle_Click(object sender, RoutedEventArgs e)
    {
        if (IsPrivate || SiteScope.SiteOf(Tabs.Active?.Address) is not { } site) return;
        var on = SiteInfoForgetToggle.IsChecked == true;
        AppData.SetForgetOnClose(site, on);
        ShowToast(on ? $"{site} será esquecido quando deixar de ter abas dele" : $"{site} deixa de ser esquecido ao fechar");
    }

    private void SiteInfoPopup_Closed(object? sender, EventArgs e)
    {
        _shredSite = null;
        SiteInfoShredConfirm.Visibility = Visibility.Collapsed;
    }

    // Primeiro mostra o que vai apagar (contado agora); só o segundo botão apaga.
    private async void SiteInfoShred_Click(object sender, RoutedEventArgs e)
    {
        if (SiteScope.SiteOf(Tabs.Active?.Address) is not { } site) return;
        _shredSite = site;
        var cookies = CountSiteCookiesAsync(site);
        var history = 0;
        if (!IsPrivate)
        {
            try { history = await Task.Run(() => AppData.History.CountSite(site)); }
            catch (Exception) { }
        }
        var cookieCount = await cookies;
        // A contagem pode demorar: entretanto o painel pode ter fechado ou passado a ser de outro site.
        if (_shredSite != site || !SiteInfoPopup.IsOpen || SiteScope.SiteOf(Tabs.Active?.Address) != site) return;
        var tabs = Tabs.Tabs.Where(t => SiteScope.Belongs(t.Address, site)).ToList();
        SiteInfoShredText.Text = SiteScope.ShredSummary(site, cookieCount, history, IsPrivate, tabs.Count,
            tabs.Count(t => t.IsLocked), tabs.Count(t => t.HasNote));
        SiteInfoShredConfirm.Visibility = Visibility.Visible;
        ShowShredConfirm();
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
        // para as fechadas recentes nem para a pilha de anulação: triturar não se anula. Os endereços delas ficam
        // para saber que origens limpar.
        var siteTabs = Tabs.Tabs.Where(t => SiteScope.Belongs(t.Address, site)).ToList();
        var seen = siteTabs.Select(t => t.Address).ToList();
        foreach (var tab in siteTabs) Tabs.CloseQuietly(tab);
        if (Tabs.Tabs.Count == 0) Tabs.NewTab(NewTabUrl);
        var result = await ShredSiteAsync(site, seen);
        ShowToast($"{site} triturado — {SiteScope.ShredDone(result.Cookies, result.History, result.Storage, IsPrivate)}");
    }
}
