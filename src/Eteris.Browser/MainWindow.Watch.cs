using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CefSharp;
using CefSharp.Wpf.HwndHost;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Shields;
using Eteris.Browser.Tabs;
using Eteris.Browser.Watching;

namespace Eteris.Browser;

// A vigia de páginas (Watching/): um relógio de 1 min na janela dona da sessão visita as vigias com a hora chegada, uma de
// cada vez, no motor escondido (WatchRunner), e larga o motor no fim de cada lote. A 1.ª ronda é 60 s depois de a janela
// abrir (as vigias em atraso correm depois de o arranque assentar). Nada corre com o Éteris fechado (decisão 2), nunca na
// janela privada, e o motor vai por omissão num contexto só em memória, sem os cookies do utilizador (decisão 1).
public partial class MainWindow
{
    private DispatcherTimer? _watchTimer;
    private WatchRunner? _watchRunner;
    private CancellationTokenSource? _watchCancel;
    private bool _watchBusy;

    // A vigia das provas ao vivo (ETERIS_TEST_WATCH) cria-se uma vez por processo, e só se ainda não existir.
    private static bool s_testWatchSeeded;

    // Só a janela dona da sessão vigia, como as abas adiadas (senão duas janelas visitavam a mesma página).
    private void WatchPages()
    {
        if (!_ownsSession || IsPrivate || _watchTimer is not null) return;
        SeedTestWatch();
        OpenTestClip();
        var cancel = _watchCancel = new CancellationTokenSource();
        var period = TestSwitches.WatchSeconds is { } seconds ? TimeSpan.FromSeconds(Math.Min(seconds, 60)) : TimeSpan.FromMinutes(1);
        _watchTimer = new DispatcherTimer(period, DispatcherPriority.Background, (_, _) => _ = CheckDueWatchesAsync(cancel.Token), Dispatcher);
        _watchTimer.Start();
        Closed += (_, _) =>
        {
            _watchTimer.Stop();
            cancel.Cancel();
            _watchRunner?.Dispose();
            _watchRunner = null;
        };
    }

    private void SeedTestWatch()
    {
        if (s_testWatchSeeded || TestSwitches.WatchSeed is not { } seed) return;
        s_testWatchSeeded = true;
        if (AppData.WatchesFor(seed.Url).Count > 0) return;
        AppData.AddWatch(new NewWatch(seed.Url, seed.Url, WatchKind.Price, seed.Selector, UseSession: false,
            WatchSchedule.MinIntervalMinutes, TargetPrice: null), IsPrivate);
    }

    // O recorte das provas ao vivo (ETERIS_TEST_CLIP), aberto uma vez por processo com o tamanho mínimo.
    private static bool s_testClipOpened;

    private void OpenTestClip()
    {
        if (s_testClipOpened || TestSwitches.ClipSeed is not { } seed) return;
        s_testClipOpened = true;
        OpenClip(seed.Url, seed.Selector, ClipLayout.Size(0, 0, SystemParameters.WorkArea));
    }

    // Um lote: as vigias com a hora chegada, uma de cada vez. Um tique a meio de um lote não começa outro.
    private async Task CheckDueWatchesAsync(CancellationToken ct)
    {
        if (_watchBusy || ct.IsCancellationRequested) return;
        var due = AppData.DueWatches(DateTime.UtcNow);
        if (due.Count == 0) return;
        _watchBusy = true;
        try
        {
            // O Escudo vale sempre nas visitas da vigia, mesmo num site onde o utilizador o desligou nas abas (a promessa
            // de eteris://privacy/ e de eteris://vigias/ di-lo: «com o Escudo ligado»).
            _watchRunner ??= new WatchRunner(WatchContext, () => new PrivacyRequestHandler(() => ShieldEngine.Current, _ => true,
                onBlocked: _ => { }, isPrivate: false, localNetworkAllowed: IsLocalNetworkAllowed, chooseCertificate: RefuseClientCertificate));
            foreach (var watch in due)
            {
                if (ct.IsCancellationRequested) break;
                await CheckWatchAsync(_watchRunner, watch, ct);
            }
        }
        finally
        {
            _watchRunner?.Release();
            _watchBusy = false;
        }
    }

    // Sem «com a minha sessão», um contexto só em memória da janela (o motor larga-o no fim do lote); com ela, o do perfil.
    private IRequestContext WatchContext(bool useSession)
        => useSession ? Cef.GetGlobalRequestContext() : CreatePrivateContext(onBlocked: null);

    // Uma verificação: ler, passar a verificação, gravar com a próxima hora e, se a vigia ainda existe, avisar.
    private async Task CheckWatchAsync(WatchRunner runner, PageWatch before, CancellationToken ct)
    {
        var (reading, block) = await runner.ReadAsync(before.Url, before.Selector, before.Kind, before.UseSession, ct);
        // A janela fechou a meio: a leitura interrompida não conta como falha.
        if (ct.IsCancellationRequested) return;
        SaveWatchCheck(before, WatchRules.ToCheck(before, reading, block, DateTime.UtcNow));
    }

    // Grava uma verificação com a hora da próxima e, se a vigia ainda existe, avisa. Serve a ronda do relógio, a leitura
    // passiva e a primeira leitura de uma vigia acabada de criar (que nunca avisa: não há leitura anterior).
    private bool SaveWatchCheck(PageWatch before, WatchCheck check)
    {
        // A agenda conta as falhas como o WatchStore as vai gravar.
        var failures = WatchRules.IsFailure(check) ? before.Failures + 1 : 0;
        var next = TestSwitches.WatchSeconds is { } seconds && check.Status is not (WatchStatus.Blocked or WatchStatus.Missing)
            ? check.AtUtc.AddSeconds(seconds)
            : WatchSchedule.Next(check.AtUtc, before.IntervalMinutes, failures, check.Status, Random.Shared);
        // A vigia foi apagada a meio da visita: nada se grava e nada se avisa.
        if (!AppData.RecordWatch(before.Id, check, next)) return false;
        NotifyWatch(before, WatchRules.Outcome(before, check), check);
        return true;
    }

    // ---- Os avisos: só dentro do Éteris (decisão 3) ----

    // Os avisos à espera de a janela voltar ao primeiro plano (um aviso dura segundos: numa janela minimizada ou por trás
    // de outra perdia-se, ficando só o piscar).
    private readonly List<(string Notice, string Url)> _pendingWatchNotices = [];
    private bool _watchNoticesWired;

    // O aviso vai para a janela normal do Éteris que está ativa (senão, esta, a dona da sessão), com «Abrir». Se essa
    // janela não está ativa, a barra de tarefas pisca e o aviso aparece quando ela voltar. Nada de ícone na bandeja nem
    // de notificações do Windows.
    private void NotifyWatch(PageWatch before, WatchOutcome outcome, WatchCheck check)
    {
        if (WatchesPage.Notice(before, outcome, check) is not { } notice) return;
        var window = NormalWindows().FirstOrDefault(w => w.IsActive && !w._forgetOnCloseDone) ?? this;
        window.DeliverWatchNotice(notice, before.Url);
    }

    private void DeliverWatchNotice(string notice, string url)
    {
        if (IsActive)
        {
            ShowWatchNotice(notice, url);
            return;
        }
        _pendingWatchNotices.Add((notice, url));
        if (!_watchNoticesWired)
        {
            _watchNoticesWired = true;
            // Depois de a janela acabar de voltar (restaurar, trazer para a frente): o aviso fica a ver-se.
            Activated += (_, _) => Dispatcher.InvokeAsync(ShowPendingWatchNotices, DispatcherPriority.Background);
        }
        WindowAttention.Flash(new System.Windows.Interop.WindowInteropHelper(this).Handle);
    }

    // Um aviso à espera mostra-se como chegou; vários mostram o último, com quantos mais houve e «Ver vigias».
    private void ShowPendingWatchNotices()
    {
        if (_pendingWatchNotices.Count == 0) return;
        var (notice, url) = _pendingWatchNotices[^1];
        var more = _pendingWatchNotices.Count - 1;
        _pendingWatchNotices.Clear();
        if (more == 0) ShowWatchNotice(notice, url);
        else
            ShowToast($"{notice} (e mais {more} {(more == 1 ? "aviso" : "avisos")} das vigias)",
                new ToastAction("Abrir", () => Tabs.NewTab(url)), new ToastAction("Ver vigias", () => OpenInternal(WatchesUrl)));
    }

    private void ShowWatchNotice(string notice, string url) => ShowToast(notice, new ToastAction("Abrir", () => Tabs.NewTab(url)));

    // «Abrir» em eteris://vigias/: a página vigiada numa aba nova desta janela (a leitura passiva lê-a lá).
    internal void OpenWatchedPage(string url)
    {
        if (IsPrivate || WebHost(url) is null) return;
        Tabs.NewTab(url);
    }

    // ---- Os gestos: «Vigiar o preço desta página» e «Avisar quando um pedaço mudar…» (menu da página e paleta) ----

    private const string WatchesUrl = WatchesPage.Url;
    private const string WatchPieceTip = "Clique no pedaço a vigiar · Esc para cancelar";
    private const string WatchPieceFailed = "Não foi possível vigiar este pedaço";
    private static readonly TimeSpan WatchScriptLimit = TimeSpan.FromSeconds(5);

    // O preço dos dados estruturados da página, se houver; senão, o utilizador aponta-o com o seletor de pedaços.
    private async void StartWatchPrice()
    {
        if (IsPrivate || _picking || ActiveBrowser is not { } browser || Tabs.Active is not { } tab || WebHost(tab.Address) is null) return;
        var url = tab.Address;
        try
        {
            var reading = await ReadTabAsync(browser, url, selector: null, WatchKind.Price, settle: false);
            if (!StillOn(tab, browser, url)) return;
            if (reading is { ChallengeMarker: false, Price: not null })
            {
                CreateWatch(tab, url, WatchKind.Price, selector: null, reading);
                return;
            }
            ShowToast("Esta página não mostra um preço: clique nele");
            if (await PickWatchPieceAsync(browser) is not { } piece || !StillOn(tab, browser, url)) return;
            var picked = await ReadTabAsync(browser, url, piece.Selector, WatchKind.Price, settle: false);
            if (!StillOn(tab, browser, url)) return;
            CreateWatch(tab, url, WatchKind.Price, piece.Selector, picked);
        }
        catch (Exception) { }
    }

    // O texto de um pedaço que o utilizador aponta: avisa quando mudar.
    private async void StartWatchChange()
    {
        if (IsPrivate || _picking || ActiveBrowser is not { } browser || Tabs.Active is not { } tab || WebHost(tab.Address) is null) return;
        var url = tab.Address;
        try
        {
            if (await PickWatchPieceAsync(browser) is not { } piece || !StillOn(tab, browser, url)) return;
            var reading = await ReadTabAsync(browser, url, piece.Selector, WatchKind.Change, settle: false);
            if (!StillOn(tab, browser, url)) return;
            CreateWatch(tab, url, WatchKind.Change, piece.Selector, reading);
        }
        catch (Exception) { }
    }

    // ---- O recorte ao vivo: «Recortar um pedaço ao vivo…» (menu da página e paleta) ----

    private const string ClipPieceTip = "Clique no pedaço a recortar · Esc para cancelar";
    private const string ClipPieceRepeated = "Este pedaço repete-se na página e não dá para o recortar sozinho · escolha outro ou Esc para cancelar";
    private static readonly string ClipLimitReached = $"Já tem {ClipLayout.MaxOpen} recortes abertos — feche um primeiro";

    // Os recortes abertos a partir desta janela: fecham com ela (sem dona, um recorte aberto manteria o Éteris a correr
    // depois de a última janela fechar — e nada corre com o Éteris fechado).
    private readonly List<ClipWindow> _clips = [];
    private bool _clipsWired;

    // O utilizador aponta um pedaço da aba; abre uma ClipWindow com ele. No máximo ClipLayout.MaxOpen em toda a aplicação
    // (contados antes de apontar e outra vez depois); nunca na janela privada.
    private async void StartClip()
    {
        if (IsPrivate || _picking || ActiveBrowser is not { } browser || Tabs.Active is not { } tab || WebHost(tab.Address) is null) return;
        if (!ClipLayout.CanOpen(ClipWindow.OpenCount))
        {
            ShowToast(ClipLimitReached);
            return;
        }
        var url = tab.Address;
        try
        {
            if (await PickWatchPieceAsync(browser, ClipPieceTip, ClipPieceRepeated) is not { } piece || !StillOn(tab, browser, url)) return;
            if (!ClipLayout.CanOpen(ClipWindow.OpenCount))
            {
                ShowToast(ClipLimitReached);
                return;
            }
            OpenClip(url, piece.Selector, ClipLayout.Size(piece.Size?.Width ?? 0, piece.Size?.Height ?? 0, SystemParameters.WorkArea));
        }
        catch (Exception) { }
    }

    // O motor do recorte vê a sessão (o contexto global, como os sites fixados na barra lateral) e tem o Escudo do site,
    // como as abas.
    private ClipWindow? OpenClip(string url, string selector, Size size)
    {
        if (IsPrivate || !ClipLayout.CanOpen(ClipWindow.OpenCount)) return null;
        var handler = new PrivacyRequestHandler(() => ShieldEngine.Current, ShieldOn, onBlocked: _ => ShieldStats.CountBlocked(persist: true),
            localNetworkAllowed: IsLocalNetworkAllowed, chooseCertificate: RefuseClientCertificate);
        var clip = new ClipWindow(url, selector, size, context: null, handler, OpenFromClip);
        _clips.Add(clip);
        clip.Closed += (_, _) => _clips.Remove(clip);
        if (!_clipsWired)
        {
            _clipsWired = true;
            Closed += (_, _) =>
            {
                foreach (var open in _clips.ToList()) open.Close();
            };
        }
        clip.Show();
        return clip;
    }

    // «Abrir a página» (ou um link aberto noutra janela, com gesto): numa aba nova desta janela, trazida para a frente.
    private void OpenFromClip(string url)
    {
        if (!IsLoaded || WebHost(url) is null) return;
        if (WindowState == WindowState.Minimized) SystemCommands.RestoreWindow(this);
        Tabs.NewTab(url);
        Activate();
    }

    // Cria a vigia com a leitura que o utilizador acabou de ver como primeira leitura (sem visita extra ao site, e a
    // próxima verificação fica para daqui a um intervalo). Uma leitura que não serve (sem preço, pedaço vazio, desafio)
    // não cria nada. A mesma vigia (endereço, tipo e pedaço) não se cria duas vezes: o aviso confirma a que já existe.
    private void CreateWatch(BrowserTab tab, string url, WatchKind kind, string? selector, PageReading? reading)
    {
        var key = WatchRules.PageUrl(url);
        var title = tab.Title is { Length: > 0 } t && t != url ? t : reading?.Title is { Length: > 0 } r ? r : key;
        var now = DateTime.UtcNow;
        var draft = new PageWatch(0, key, "", title, kind, selector, UseSession: false, WatchSchedule.DefaultIntervalMinutes,
            TargetPrice: null, Paused: false, WatchStatus.New, Failures: 0, now, LastCheckUtc: null, now, LastText: null,
            LastPrice: null, Currency: null, FailingSinceUtc: null, PageBlock.None);
        if (WatchRules.Passive(draft, reading, PageBlock.None, now) is not { } check)
        {
            ShowToast(WatchPieceFailed);
            return;
        }
        if (AppData.WatchesFor(key).FirstOrDefault(w => w.Kind == kind && w.Selector == selector) is { } existing)
        {
            // A que já existe e estava em pausa volta a vigiar: o aviso diz «A vigiar…».
            if (existing.Paused) AppData.PauseWatch(existing.Id, false);
        }
        else
        {
            if (AppData.AddWatch(new NewWatch(key, title, kind, selector, UseSession: false, WatchSchedule.DefaultIntervalMinutes, TargetPrice: null), IsPrivate)
                is not { } id)
            {
                ShowToast(WatchPieceFailed);
                return;
            }
            SaveWatchCheck(draft with { Id = id }, check);
        }
        var seeWatches = new ToastAction("Ver vigias", () => OpenInternal(WatchesUrl));
        if (kind == WatchKind.Price)
            ShowToast($"A vigiar o preço: {PriceText.Format(check.Price!.Value, check.Currency)} — avisa quando baixar", seeWatches);
        else
            ShowToast("A vigiar este pedaço — avisa quando mudar", seeWatches);
    }

    // O seletor de pedaços na aba (o mesmo construtor de seletores do «Esconder um elemento»). Esc ou o tempo esgotado
    // não fazem nada; uma resposta que não serve (a página pode mexer nela) diz que não foi possível. Devolve também o
    // tamanho do pedaço (px CSS), que o recorte ao vivo usa.
    private async Task<(string Selector, string Text, Size? Size)?> PickWatchPieceAsync(ChromiumWebBrowser browser, string tip = WatchPieceTip,
        string? repeated = null)
    {
        _picking = true;
        try
        {
            Keyboard.Focus(browser);
            var response = await PageScript.EvaluateAsync(browser, WatchScripts.PickPiece(tip, repeated), TimeSpan.FromMinutes(3),
                userGesture: true, asyncBody: true);
            if (!response.Success || response.Result is null) return null;
            var json = response.Result as string;
            if (WatchReadingParser.Pick(json) is { } piece) return (piece.Selector, piece.Text, WatchReadingParser.PickSize(json));
            ShowToast(WatchPieceFailed);
            return null;
        }
        finally
        {
            _picking = false;
        }
    }

    // Lê a página da aba com o script da vigia. Com «settle», espera (até 5 s, de 1 em 1 s) que a leitura sirva, como o
    // motor da vigia: uma página que põe o preço por JavaScript ainda pode não o ter.
    private static async Task<PageReading?> ReadTabAsync(ChromiumWebBrowser browser, string url, string? selector, WatchKind kind, bool settle)
    {
        PageReading? reading = null;
        for (var attempt = 0; attempt < (settle ? 6 : 1); attempt++)
        {
            if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(1));
            if (browser.IsDisposed || WatchRules.PageUrl(browser.Address) != WatchRules.PageUrl(url)) return null;
            var result = await PageScript.EvaluateAsync(browser, WatchScripts.Read(selector), WatchScriptLimit);
            if (result.Success && result.Result is string json) reading = WatchReadingParser.Parse(json, url);
            if (reading is not null && WatchRules.Settled(kind, reading)) break;
        }
        return reading;
    }

    private bool StillOn(BrowserTab tab, ChromiumWebBrowser browser, string url)
        => ReferenceEquals(Tabs.Active, tab) && !browser.IsDisposed && WatchRules.PageUrl(browser.Address) == WatchRules.PageUrl(url);

    // ---- A leitura passiva: o utilizador abriu numa aba uma página vigiada ----

    // Lê-a ali, sem visita extra ao site — a única leitura das vigias bloqueadas e sem pedaço (decisão 8) — e grava como
    // a ronda do relógio. Só uma leitura boa conta (WatchRules.Passive): a aba do utilizador a meio de carregar ou num
    // desafio nunca põe a vigia em falha. As vigias em pausa ficam como estão. Nunca na janela privada.
    private async void ReadWatchedPagePassively(ChromiumWebBrowser browser, string url, int httpStatus)
    {
        if (IsPrivate || WebHost(url) is null) return;
        try
        {
            var key = WatchRules.PageUrl(url);
            // Corre a cada página carregada: a consulta sai do fio da interface e nada mais se faz sem vigias.
            var watches = await Task.Run(() => AppData.WatchesFor(key));
            if (!watches.Any(w => !w.Paused)) return;
            await Task.Delay(TimeSpan.FromSeconds(3));
            foreach (var watch in watches)
            {
                if (browser.IsDisposed || WatchRules.PageUrl(browser.Address) != key) return;
                // A vigia fresca: a ronda do relógio pode tê-la gravado (ou o utilizador apagado ou pausado) entretanto.
                if (AppData.WatchesFor(key).FirstOrDefault(w => w.Id == watch.Id) is not { Paused: false } before) continue;
                var reading = await ReadTabAsync(browser, url, before.Selector, before.Kind, settle: true);
                if (reading is null) return;
                var block = WatchRules.Classify(httpStatus, url, reading.FinalUrl, reading.Title, reading.ChallengeMarker);
                if (WatchRules.Passive(before, reading, block, DateTime.UtcNow) is { } check) SaveWatchCheck(before, check);
            }
        }
        catch (Exception) { }
    }
}
