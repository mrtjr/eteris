using System.IO;
using System.Windows.Threading;
using CefSharp;
using Eteris.Browser.Data;
using Eteris.Browser.Shields;
using Eteris.Browser.Tabs;

namespace Eteris.Browser;

// Esquecer os sites de salto (Shields/BounceTracking.cs): cada aba segue a sua cadeia de navegações; os sites que só
// serviram de salto ficam marcados e, uma hora depois e sem visita a sério, perdem os cookies e o armazenamento.
// Só nas janelas normais: a janela privada esquece tudo ao fechar.
public partial class MainWindow
{
    private static readonly string BounceFile = Path.Combine(Infrastructure.AppPaths.Root, "saltos.json");

    private static readonly Lazy<BounceLedger> s_bounceLedger = new(() =>
    {
        string? json = null;
        try { if (File.Exists(BounceFile)) json = File.ReadAllText(BounceFile); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        var ledger = BounceLedger.Load(json, () => DateTime.UtcNow);
        ledger.Enabled = Data.Settings.Current.ForgetBounceTrackers;
        return ledger;
    });

    internal static BounceLedger Bounces => s_bounceLedger.Value;

    private static DispatcherTimer? s_bounceTimer;
    private static bool s_bounceRunning;

    private readonly Dictionary<BrowserTab, RedirectChain> _chains = [];

    private RedirectChain ChainOf(BrowserTab tab)
    {
        if (!_chains.TryGetValue(tab, out var chain)) _chains[tab] = chain = new RedirectChain(TrackerBlocker.Default.Lists);
        return chain;
    }

    private void StartBounceWatch()
    {
        if (IsPrivate) return;
        // Uma aba fechada leva a sua cadeia.
        Tabs.Tabs.CollectionChanged += (_, e) =>
        {
            foreach (var tab in e.OldItems?.OfType<BrowserTab>() ?? []) _chains.Remove(tab);
        };
        Closed += (_, _) => SaveBounces();
        if (s_bounceTimer is not null) return;
        // Desligar «Esquecer os sites de salto» esquece o que o Éteris sabia e apaga saltos.json; ligar recomeça do zero.
        Data.Settings.Changed += settings => System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (Bounces.Enabled == settings.ForgetBounceTrackers) return;
            Bounces.Enabled = settings.ForgetBounceTrackers;
            SaveBounces();
        });
        // Um só relógio para todas as janelas: o primeiro passa ao fim de um minuto, depois a cada cinco.
        s_bounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        s_bounceTimer.Tick += async (_, _) =>
        {
            s_bounceTimer.Interval = TimeSpan.FromMinutes(5);
            await ClearDueBouncesAsync();
        };
        s_bounceTimer.Start();
    }

    // Apagar o histórico apaga também as visitas que o Éteris registou desde então (pode chegar de outra thread).
    internal static void ForgetBounceVisits(DateTime sinceUtc)
        => System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            Bounces.ForgetInteractionsSince(sinceUtc);
            SaveBounces();
        });

    // Chega na thread do CEF (OnBeforeBrowse da página principal).
    private void OnMainNavigation(BrowserTab tab, MainNavigation nav)
    {
        if (IsPrivate) return;
        Dispatcher.InvokeAsync(() =>
        {
            var chain = ChainOf(tab);
            var now = DateTime.UtcNow;
            if (RedirectChain.Continues(nav.UserGesture, nav.IsRedirect, nav.Transition))
            {
                MarkBounces(chain.Continue(nav.CurrentUrl, nav.Url, now, nav.IsRedirect, nav.Rewritten), tab: null);
                return;
            }
            // Uma navegação nova: a lista do Escudo é desta página, não da anterior.
            tab.ClearBounceSites();
            MarkBounces(chain.Begin(nav.CurrentUrl, nav.Url, now), tab: null);
            if (RedirectChain.IsTyped(nav.Transition) && SiteScope.SiteOf(nav.Url) is { } typed) Bounces.RecordInteraction(typed);
        });
    }

    // Um clique ou tecla na página: o site foi visitado a sério e a cadeia que trouxe até aqui termina.
    private void OnPageInteraction(BrowserTab tab, string? url)
    {
        if (IsPrivate) return;
        if (SiteScope.SiteOf(url) is { } site) Bounces.RecordInteraction(site);
        if (_chains.TryGetValue(tab, out var chain) && chain.Hops > 1) MarkBounces(chain.Close(), tab);
    }

    // A página acabou de carregar: se ninguém a levar para outro lado nos próximos segundos, a cadeia termina aqui e
    // os saltos aparecem no Escudo desta aba.
    private async void SettleChainLater(BrowserTab tab)
    {
        if (IsPrivate || !_chains.TryGetValue(tab, out var chain) || chain.Hops < 2) return;
        var version = chain.Version;
        await Task.Delay(RedirectChain.DwellLimit);
        if (chain.Version != version || !_chains.ContainsKey(tab)) return;
        MarkBounces(chain.Close(), tab);
    }

    private void MarkBounces(IReadOnlyList<BounceSite> sites, BrowserTab? tab)
    {
        if (sites.Count == 0) return;
        var marked = sites.Where(b => Bounces.RecordBounce(b.Site, b.Origins)).Select(b => b.Site).ToList();
        if (tab is null || marked.Count == 0) return;
        tab.RecordBounceSites(marked);
        if (ReferenceEquals(tab, Tabs.Active)) UpdateShieldButton();
    }

    private static void SaveBounces()
    {
        if (!s_bounceLedger.IsValueCreated) return;
        if (!Bounces.Enabled)
        {
            // Desligado: nada do que o utilizador visitou fica no perfil.
            try { File.Delete(BounceFile); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return;
        }
        if (!Bounces.Dirty) return;
        try { File.WriteAllText(BounceFile, Bounces.Serialize()); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // Os sites com o prazo vencido perdem os cookies e o armazenamento; os que têm uma aba aberta esperam.
    private static async Task ClearDueBouncesAsync()
    {
        if (s_bounceRunning) return;
        s_bounceRunning = true;
        try
        {
            if (!Data.Settings.Current.ForgetBounceTrackers || NormalWindows().FirstOrDefault() is not { } window)
            {
                SaveBounces();
                return;
            }
            var open = OpenAddresses().ToList();
            var due = Bounces.Due(site => SiteScope.StillOpen(site, open));
            var cleared = new List<string>();
            foreach (var site in due)
            {
                // O armazenamento apaga-se por origem: as que a cadeia visitou (clk.rastreador.com, não só
                // rastreador.com e www.rastreador.com) e as do histórico, como no Triturar.
                var seen = new List<string>(Bounces.OriginsOf(site));
                try { seen.AddRange(await Task.Run(() => AppData.History.SiteUrls(site))); }
                catch (Exception) { }
                // Sem motor pronto o armazenamento fica (ou os cookies, ou o perfil do motor de vídeo): o site continua à
                // espera da próxima volta.
                var done = await window.ClearSiteStorageAsync(site, seen);
                if (!done.Storage || done.CookiesFailed || done.VideoEngineFailed) continue;
                Bounces.MarkCleared(site);
                Shields.PrivacyLedger.Count(Shields.PrivacyCategory.Forgotten, site);
                cleared.Add(site);
            }
            SaveBounces();
            if (cleared.Count > 0) window.ShowToast(BounceText.Toast(cleared));
        }
        finally { s_bounceRunning = false; }
    }
}
