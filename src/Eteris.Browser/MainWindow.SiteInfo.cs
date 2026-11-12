using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CefSharp;
using CefSharp.Wpf.HwndHost;
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
        if (!SiteInfoPopup.IsOpen) FitSiteInfoToScreen();
        SiteInfoPopup.IsOpen = !SiteInfoPopup.IsOpen;
        // O clique fora do cartão chega à raiz do Popup (que tem o rato), não ao conteúdo.
        if (SiteInfoPopup.IsOpen && SiteInfoPopup.Child is { } card && PresentationSource.FromVisual(card)?.RootVisual is UIElement root)
        {
            root.PreviewMouseDown -= SiteInfoCard_PreviewMouseDown;
            root.PreviewMouseDown += SiteInfoCard_PreviewMouseDown;
        }
    }

    // O cartão é um Popup com StaysOpen=False: enquanto está aberto fica com o rato, e o clique fora dele só o fecha.
    // Com o cartão aberto, o 1.º clique noutra aba só fechava o cartão e a aba só mudava ao 2.º (medido ao vivo; o
    // Ctrl+Tab mudava à primeira). Como no Chrome, o clique que fecha o cartão também escolhe a aba onde caiu.
    private void SiteInfoCard_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || SiteInfoPopup.Child is not FrameworkElement card) return;
        var inCard = e.GetPosition(card);
        if (new Rect(card.RenderSize).Contains(inCard) || PresentationSource.FromVisual(this) is null) return;
        var point = PointFromScreen(card.PointToScreen(inCard));
        if (InputHitTest(point) is not DependencyObject hit) return;
        if (ItemsControl.ContainerFromElement(TabList, hit) is not FrameworkElement { DataContext: BrowserTab tab } item) return;
        // O «x» e a etiqueta do grupo têm as suas próprias ações: só a aba em si.
        if (IsInsideTabControl(hit, item) || ReferenceEquals(Tabs.Active, tab)) return;
        Tabs.Active = tab;
    }

    // O cartão nunca passa do fundo da área de trabalho do monitor onde está o cadeado: o que não cabe rola por dentro.
    private void FitSiteInfoToScreen()
    {
        SiteInfoScroll.ScrollToTop();
        if (PresentationSource.FromVisual(SiteInfoButton) is not { CompositionTarget: { } target }) return;
        var below = SiteInfoButton.PointToScreen(new Point(0, SiteInfoButton.ActualHeight));
        var monitor = MonitorFromPoint(new NativePoint((int)below.X, (int)below.Y), MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info)) return;
        var room = target.TransformFromDevice.Transform(new Vector(0, info.WorkBottom - below.Y)).Y;
        // Afastamento do Popup (6), moldura e enchimento do cartão (2 + 36), margem da sombra (44) e folga.
        SiteInfoScroll.MaxHeight = Math.Max(220, room - 6 - 38 - 44 - 10);
    }

    // A confirmação de «Triturar» fica no fundo do cartão: aparece sempre à vista.
    private void ShowShredConfirm()
        => Dispatcher.InvokeAsync(() => SiteInfoShredConfirm.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public int MonitorLeft, MonitorTop, MonitorRight, MonitorBottom;
        public int WorkLeft, WorkTop, WorkRight, WorkBottom;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(NativePoint point, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

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
        // O limite do referrer só se aplica com o Escudo ligado no site (PrivacyRequestHandler).
        var shieldOff = Uri.TryCreate(tab.Address, UriKind.Absolute, out var page) && !ShieldOn(page.Host);
        SiteKnowledge.Facts Facts(IReadOnlyList<DateTime?> cookies, long? storage) => new(cookies, storage, snap.ThirdParties.Count,
            groups.Count(g => g.Known), snap.ThirdParties.Count(d => d.Blocked > 0), referrer, tab.LocalNetworkBlocked.Sum(p => p.Value), DateTime.Now,
            shieldOff);
        SiteKnowledgeList.ItemsSource = SiteKnowledge.Describe(Facts([], null)).Skip(2).ToList();

        var site = Data.SiteScope.SiteOf(tab.Address);
        IReadOnlyList<DateTime?> expiries = [];
        var seen = new List<string> { tab.Address };
        long? storage = null;
        try
        {
            if (site is not null)
            {
                var cookies = (await CookieContext.GetCookieManager(null).VisitAllCookiesAsync())
                    .Where(c => Data.SiteScope.CookieBelongs(c.Domain, site)).ToList();
                expiries = cookies.Select(c => c.Expires).ToList();
                seen.AddRange(cookies.Select(c => "https://" + c.Domain.Trim().TrimStart('.') + "/"));
            }
        }
        catch (Exception) { }
        try
        {
            if (ActiveBrowser is { } browser && ReferenceEquals(Tabs.Active, tab))
            {
                // Todas as origens do site que se conhecem (a página, com e sem www, e as dos cookies), e o armazenamento
                // local (localStorage), que a quota do Chromium não conta.
                var origins = site is null ? [info.Origin] : Data.SiteScope.Origins(site, seen).Take(12).ToList();
                var call = MeasureStorageAsync(browser, origins);
                if (await Task.WhenAny(call, Task.Delay(2000)) == call) storage = await call;
            }
        }
        catch (Exception) { }
        if (!SiteInfoPopup.IsOpen || !ReferenceEquals(Tabs.Active, tab)) return;
        SiteKnowledgeList.ItemsSource = SiteKnowledge.Describe(Facts(expiries, storage));
    }

    // Bases de dados, cache e trabalhadores (quota do Chromium) mais o localStorage, somados por origem; null se o
    // motor não respondeu a nenhuma.
    private static async Task<long?> MeasureStorageAsync(ChromiumWebBrowser browser, IReadOnlyList<string> origins)
    {
        using var client = browser.GetDevToolsClient();
        long total = 0;
        var answered = false;
        foreach (var origin in origins)
        {
            try
            {
                total += (long)(await DevToolsCalls.Run(() => client.Storage.GetUsageAndQuotaAsync(origin))).Usage;
                answered = true;
            }
            catch (Exception) { }
            try
            {
                // Pelo método em bruto: o tipo gerado do CefSharp lê «entries» (pares chave/valor) como uma lista simples.
                var response = await DevToolsCalls.Run(() => client.ExecuteDevToolsMethodAsync("DOMStorage.getDOMStorageItems", new Dictionary<string, object>
                {
                    ["storageId"] = new Dictionary<string, object> { ["securityOrigin"] = origin, ["isLocalStorage"] = true },
                }));
                if (response.Success)
                {
                    using var json = System.Text.Json.JsonDocument.Parse(response.ResponseAsJsonString);
                    total += Data.SiteScope.LocalStorageBytes(json.RootElement);
                    answered = true;
                }
            }
            catch (Exception) { }
        }
        return answered ? total : null;
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
        var origin = SiteInfo.Describe(tab.Address).Origin;
        SitePermissions.Forget(PermissionRules.Site(origin), kind);
        // Também a autorização desta sessão e a decisão que o próprio Chromium tenha guardado (MOT-03).
        PermissionGrants.Forget(PermissionRules.Site(origin), kind);
        EnginePermissions.Reset(CookieContext, origin, [kind]);
        FillSiteInfo();
        ShowToast($"{Permissions.PermissionText.Name(kind)}: o site voltará a perguntar");
    }

    // Apaga cookies (do contexto desta janela) e armazenamento do site (domínio registável e subdomínios, como o
    // Triturar); depois recarrega. Não fecha abas nem toca no histórico nem nas permissões.
    private async void SiteInfoClear_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.Active is not { } tab || ActiveBrowser is not { } browser) return;
        var info = SiteInfo.Describe(tab.Address);
        SiteInfoPopup.IsOpen = false;
        try
        {
            if (Data.SiteScope.SiteOf(tab.Address) is not { } site)
            {
                // Uma página que não é da web: só a origem dela.
                using var own = browser.GetDevToolsClient();
                await DevToolsCalls.Run(() => own.Storage.ClearDataForOriginAsync(info.Origin, "all"));
                ShowToast($"Dados de {info.Host} apagados");
                ReloadActive();
                return;
            }

            var (deleted, hosts, cookiesFailed) = await DeleteSiteCookiesAsync(CookieContext.GetCookieManager(null), site);
            var origins = Data.SiteScope.Origins(site, [tab.Address, .. hosts.SelectMany(h => new[] { "https://" + h + "/", "http://" + h + "/" })]);
            var storage = true;
            try
            {
                using var client = browser.GetDevToolsClient();
                foreach (var origin in origins)
                {
                    // Uma origem recusada pelo motor não impede as outras, mas o aviso di-lo.
                    try { await DevToolsCalls.Run(() => client.Storage.ClearDataForOriginAsync(origin, "all")); }
                    catch (Exception) { storage = false; }
                }
            }
            catch (Exception) { storage = false; }
            // O perfil do motor de vídeo, com a regra do Triturar (MainWindow.Forget.cs): numa janela normal, com uma aba
            // viva desse motor ou para um site que lá abriu.
            var videoFailed = false;
            if (!IsPrivate && VideoEngine.ClearsSite(LiveVideoCore() is not null, startupSweep: false, pending: false,
                    VideoEngine.MayHoldData(site, Data.AppData.IsVideoSite(site) || Data.AppData.IsVideoSite(site, platformCodecs: true), Data.AppData.IsVideoOptOut(site))))
            {
                var video = await ClearVideoSiteAsync(site, origins);
                deleted += video.Cookies;
                videoFailed = !video.Complete;
            }
            ShowToast(Data.SiteScope.SiteDataCleared(site, deleted, storage, cookiesFailed, videoFailed));
            ReloadActive();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            ShowToast("Não foi possível apagar os dados deste site");
        }
    }
}
