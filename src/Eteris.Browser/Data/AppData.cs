using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Data;

public static class AppData
{
    private static readonly Lazy<HistoryStore> LazyHistory = new(() => new HistoryStore(AppPaths.Database));
    private static readonly Lazy<ShieldExceptionStore?> LazyShieldExceptions = new(() =>
    {
        try { return new ShieldExceptionStore(AppPaths.Database); }
        catch (Exception) { return null; }
    });

    private static readonly Lazy<SiteZoomStore?> LazyZoom = new(() =>
    {
        try { return new SiteZoomStore(AppPaths.Database); }
        catch (Exception) { return null; }
    });

    public static HistoryStore History => LazyHistory.Value;

    private static readonly Lazy<HistoryWriter> LazyWriter = new(() => new HistoryWriter(() => History, TimeSpan.FromSeconds(1)));

    // Visitas e ícones: escritos em segundo plano, pela ordem (Data/HistoryWriter.cs).
    public static void QueueWrite(Action<HistoryStore> write) => LazyWriter.Value.Enqueue(write);

    // Título de uma visita: só o último de cada segundo é escrito (sites com contadores no título).
    public static void UpdateTitleLater(string url, string title) => LazyWriter.Value.UpdateTitleLater(url, title);

    // Antes de ler ou apagar, e ao sair: o que está na fila fica escrito.
    public static void FlushHistory()
    {
        if (LazyWriter.IsValueCreated) LazyWriter.Value.Flush(TimeSpan.FromSeconds(2));
    }

    public static void CloseHistory()
    {
        if (LazyWriter.IsValueCreated) LazyWriter.Value.Dispose();
    }

    private static readonly Lazy<HiddenElementsStore?> LazyHidden = new(() =>
    {
        try { return new HiddenElementsStore(AppPaths.Database); }
        catch (Exception) { return null; }
    });

    public static IReadOnlyList<string> HiddenElements(string host)
    {
        try { return LazyHidden.Value?.For(host) ?? []; }
        catch (Exception) { return []; }
    }

    public static void HideElement(string host, string selector)
    {
        try { LazyHidden.Value?.Add(host, selector); }
        catch (Exception) { }
    }

    public static void ClearHiddenElements(string host)
    {
        try { LazyHidden.Value?.Clear(host); }
        catch (Exception) { }
    }

    private static readonly Lazy<DailyStatsStore?> LazyDaily = new(() =>
    {
        try { return new DailyStatsStore(AppPaths.Database); }
        catch (Exception) { return null; }
    });

    private static Timer? _statsTimer;

    // Grava os bloqueios em lote (a cada 20 s e à saída), nunca a cada pedido.
    public static void StartShieldStats() => _statsTimer ??= new Timer(_ => FlushShieldStats(), null, 20_000, 20_000);

    public static void FlushShieldStats()
    {
        var pending = Shields.ShieldStats.TakePending();
        if (pending > 0)
        {
            try { LazyDaily.Value?.Add(DateTime.Today, pending); }
            catch (Exception) { }
        }
        FlushPrivacyEvents();
    }

    // Relatório de privacidade (eteris://privacy/): agregados por dia, categoria e domínio do rastreador.
    private static readonly Lazy<PrivacyEventsStore?> LazyPrivacyEvents = new(() =>
    {
        try { return new PrivacyEventsStore(AppPaths.Database); }
        catch (Exception) { return null; }
    });

    private static DateOnly _privacyPruned;

    private static void FlushPrivacyEvents()
    {
        var events = Shields.PrivacyLedger.Take();
        var today = DateOnly.FromDateTime(DateTime.Today);
        try
        {
            if (LazyPrivacyEvents.Value is not { } store) return;
            store.Add(today, events);
            if (_privacyPruned == today) return;
            store.Prune(today);
            _privacyPruned = today;
        }
        catch (Exception) { }
    }

    public static IReadOnlyList<Shields.PrivacyEvent> PrivacyEvents(int days)
    {
        FlushShieldStats();
        try { return LazyPrivacyEvents.Value?.Since(DateOnly.FromDateTime(DateTime.Today).AddDays(-(days - 1))) ?? []; }
        catch (Exception) { return []; }
    }

    // «Apagar o relatório»: os agregados do relatório e o painel semanal da nova aba (os mesmos números).
    public static void ClearPrivacyReport()
    {
        Shields.PrivacyLedger.Take();
        Shields.ShieldStats.TakePending();
        try
        {
            LazyPrivacyEvents.Value?.Clear();
            LazyDaily.Value?.Clear();
        }
        catch (Exception) { }
    }

    public static int[] ShieldWeek()
    {
        FlushShieldStats();
        try { return LazyDaily.Value?.Week(DateTime.Today) ?? new int[7]; }
        catch (Exception) { return new int[7]; }
    }

    public static double SiteZoom(string host) => LazyZoom.Value?.Get(host) ?? 0;

    public static void SetSiteZoom(string host, double level)
    {
        try { LazyZoom.Value?.Set(host, level); }
        catch (Exception) { }
    }

    public static bool IsShieldOn(string host) => LazyShieldExceptions.Value?.Contains(host) != true;

    public static void SetShield(string host, bool on)
    {
        if (LazyShieldExceptions.Value is not { } store) return;
        try
        {
            if (on) store.Remove(host);
            else store.Add(host);
        }
        catch (Exception) { }
    }

    // O histórico é conveniência: uma falha da base de dados nunca pode interromper a navegação.
    public static void TryWrite(Action<HistoryStore> write)
    {
        FlushHistory();
        try { write(History); }
        catch (Exception) { }
    }

    public static IReadOnlyList<T> TryRead<T>(Func<HistoryStore, IReadOnlyList<T>> read)
    {
        FlushHistory();
        try { return read(History); }
        catch (Exception) { return []; }
    }

    public static IReadOnlyDictionary<string, string> SiteIcons(IEnumerable<string> urls)
    {
        FlushHistory();
        try { return History.SiteIcons(urls); }
        catch (Exception) { return new Dictionary<string, string>(); }
    }

    private static readonly Lazy<BookmarkStore> LazyBookmarks = new(() => new BookmarkStore(AppPaths.Database));

    public static IReadOnlyList<Bookmark> Bookmarks(Func<BookmarkStore, IReadOnlyList<Bookmark>> read)
    {
        try { return read(LazyBookmarks.Value); }
        catch (Exception) { return []; }
    }

    private static readonly Lazy<ReadingListStore?> LazyReading = new(() =>
    {
        try { return new ReadingListStore(AppPaths.Database); }
        catch (Exception) { return null; }
    });

    public static IReadOnlyList<ReadingItem> ReadingList()
    {
        try { return LazyReading.Value?.All() ?? []; }
        catch (Exception) { return []; }
    }

    public static void SaveForLater(string url, string title)
    {
        try { LazyReading.Value?.Add(url, title, DateTime.UtcNow); }
        catch (Exception) { }
    }

    public static bool IsSavedForLater(string url)
    {
        try { return LazyReading.Value?.Contains(url) == true; }
        catch (Exception) { return false; }
    }

    public static void MarkRead(string url, bool read)
    {
        try { LazyReading.Value?.MarkRead(url, read); }
        catch (Exception) { }
    }

    public static void RemoveFromReadingList(string url)
    {
        try { LazyReading.Value?.Remove(url); }
        catch (Exception) { }
    }

    // Tira e devolve a linha (para o «Anular» a repor com a data e o estado lido).
    public static ReadingItem? TakeFromReadingList(string url)
    {
        try
        {
            if (LazyReading.Value is not { } store || store.Item(url) is not { } item) return null;
            store.Remove(url);
            return item;
        }
        catch (Exception) { return null; }
    }

    public static bool RestoreReadingItem(ReadingItem item)
    {
        try { return LazyReading.Value?.Restore(item) == true; }
        catch (Exception) { return false; }
    }

    private static readonly Lazy<SavedSessionsStore?> LazySessions = new(() =>
    {
        try { return new SavedSessionsStore(AppPaths.Database); }
        catch (Exception) { return null; }
    });

    private static readonly Lazy<DoneTabsStore?> LazyDone = new(() =>
    {
        try { return new DoneTabsStore(AppPaths.Database); }
        catch (Exception) { return null; }
    });

    // Devolve false quando não ficou gravado (base de dados indisponível): quem fecha a aba a seguir tem de o saber.
    public static bool MarkDone(string url, string title)
    {
        try
        {
            if (LazyDone.Value is not { } store) return false;
            store.Add(url, title, DateTime.UtcNow);
            return true;
        }
        catch (Exception) { return false; }
    }

    // «Anular» de um Feito: a entrada mais recente desse endereço sai de «Feitas hoje».
    public static void UndoDone(string url)
    {
        try
        {
            if (LazyDone.Value is not { } store) return;
            if (store.Since(DateTime.UtcNow.AddDays(-1)).Where(d => d.Url == url).MaxBy(d => d.Id) is { } done) store.Delete(done.Id);
        }
        catch (Exception) { }
    }

    public static IReadOnlyList<DoneTab> DoneSince(DateTime sinceUtc)
    {
        try { return LazyDone.Value?.Since(sinceUtc) ?? []; }
        catch (Exception) { return []; }
    }

    private static readonly Lazy<SnoozedTabsStore?> LazySnoozed = new(() =>
    {
        try { return new SnoozedTabsStore(AppPaths.Database); }
        catch (Exception) { return null; }
    });

    // Adiar, esquecer a adiada e arquivar devolvem false quando a base de dados não gravou: a aba não pode sair da
    // faixa (nem voltar outra vez a cada minuto) por causa de uma escrita que falhou em silêncio.
    public static bool SnoozeTab(string url, string title, string? name, string? note, DateTime dueUtc)
    {
        try
        {
            if (LazySnoozed.Value is not { } store) return false;
            store.Add(url, title, name, note, dueUtc);
            return true;
        }
        catch (Exception) { return false; }
    }

    public static bool ForgetSnoozed(long id)
    {
        try
        {
            if (LazySnoozed.Value is not { } store) return false;
            store.Delete(id);
            return true;
        }
        catch (Exception) { return false; }
    }

    public static IReadOnlyList<SnoozedTab> SnoozedTabs()
    {
        try { return LazySnoozed.Value?.All() ?? []; }
        catch (Exception) { return []; }
    }

    public static IReadOnlyList<SnoozedTab> SnoozedDue(DateTime nowUtc)
    {
        try { return LazySnoozed.Value?.Due(nowUtc) ?? []; }
        catch (Exception) { return []; }
    }

    private static readonly Lazy<ArchivedTabsStore?> LazyArchived = new(() =>
    {
        try { return new ArchivedTabsStore(AppPaths.Database); }
        catch (Exception) { return null; }
    });

    public static bool ArchiveTab(string url, string title, string? name, DateTime lastActiveUtc, DateTime nowUtc)
    {
        try
        {
            if (LazyArchived.Value is not { } store) return false;
            store.Add(url, title, name, lastActiveUtc, nowUtc);
            return true;
        }
        catch (Exception) { return false; }
    }


    public static void ForgetArchived(long id)
    {
        try { LazyArchived.Value?.Delete(id); }
        catch (Exception) { }
    }

    public static IReadOnlyList<ArchivedTab> ArchivedTabs()
    {
        try { return LazyArchived.Value?.All() ?? []; }
        catch (Exception) { return []; }
    }

    public static IReadOnlyList<SavedSession> SavedSessions()
    {
        try { return LazySessions.Value?.All() ?? []; }
        catch (Exception) { return []; }
    }

    public static bool SaveSession(string name, IReadOnlyList<SavedTab> tabs)
    {
        try
        {
            if (LazySessions.Value is not { } store) return false;
            store.Save(name, tabs, DateTime.UtcNow);
            return true;
        }
        catch (Exception) { return false; }
    }


    public static bool RenameSession(long id, string name)
    {
        try { return LazySessions.Value?.Rename(id, name) == true; }
        catch (Exception) { return false; }
    }

    public static void DeleteSession(long id)
    {
        try { LazySessions.Value?.Delete(id); }
        catch (Exception) { }
    }

    public static int ImportBookmarks(IReadOnlyList<Bookmark> marks)
    {
        try { return BookmarkImport.Import(LazyBookmarks.Value, marks, DateTime.UtcNow); }
        catch (Exception) { return 0; }
    }

    private static readonly Lazy<Permissions.SitePermissionStore?> LazyPermissions = new(() =>
    {
        try { return new Permissions.SitePermissionStore(AppPaths.Database); }
        catch (Exception) { return null; }
    });

    // «Definições por site»: zoom, escudo, permissões e elementos escondidos de cada site.
    public static IReadOnlyList<SiteSummary> SiteSummaries()
    {
        try
        {
            return SiteOverview.Build(
                LazyZoom.Value?.All() ?? new Dictionary<string, double>(),
                LazyShieldExceptions.Value?.All() ?? [],
                LazyPermissions.Value?.AllSites() ?? new Dictionary<string, IReadOnlyList<(Permissions.SitePermission, bool)>>(),
                LazyHidden.Value?.Hosts() ?? new Dictionary<string, int>(),
                LazyStrict.Value?.All() ?? [],
                LazyDark.Value?.All() ?? [],
                LazyHttp.Value?.All() ?? []);
        }
        catch (Exception) { return []; }
    }

    public static void ResetSite(string site)
    {
        foreach (var host in new[] { site, "www." + site })
        {
            try
            {
                LazyZoom.Value?.Set(host, 0);
                LazyPermissions.Value?.Forget(host);
                LazyHidden.Value?.Clear(host);
            }
            catch (Exception) { }
            SetHttpException(host, false);
            Shields.ShieldEngine.Https.Forget(host);
            Shields.LocalNetworkGrants.Forget(host);
        }
        SetForgetOnClose(site, false);
        Shields.SiteRules.Forget(site);
        SetShield(site, true);
        SetDark(site, false);
        SetStrict(site, false);
        SetVideoSite(site, false);
        SetVideoOptOut(site, false);
    }

    private static readonly Lazy<StrictSiteStore?> LazyStrict = new(() =>
    {
        try { return new StrictSiteStore(AppPaths.Database); }
        catch (Exception) { return null; }
    });

    public static bool IsStrict(string host) => LazyStrict.Value?.Contains(host) == true;

    public static void SetStrict(string host, bool on)
    {
        try
        {
            if (on) LazyStrict.Value?.Add(host);
            else LazyStrict.Value?.Remove(host);
        }
        catch (Exception) { }
    }

    private static readonly Lazy<StrictSiteStore?> LazyDark = new(() =>
    {
        try { return new StrictSiteStore(AppPaths.Database, "dark_sites"); }
        catch (Exception) { return null; }
    });

    public static bool IsDark(string host) => LazyDark.Value?.Contains(host) == true;

    public static void SetDark(string host, bool on)
    {
        try
        {
            if (on) LazyDark.Value?.Add(host);
            else LazyDark.Value?.Remove(host);
        }
        catch (Exception) { }
    }

    // «HTTPS primeiro»: hosts que o utilizador aceitou abrir sempre por http:// («Sempre para este site»).
    private static readonly Lazy<StrictSiteStore?> LazyHttp = new(() =>
    {
        try { return new StrictSiteStore(AppPaths.Database, "http_sites", exactHost: true); }
        catch (Exception) { return null; }
    });

    public static bool IsHttpException(string host) => LazyHttp.Value?.Contains(host) == true;

    public static IReadOnlyList<string> HttpExceptions() => LazyHttp.Value?.All() ?? [];

    public static void SetHttpException(string host, bool on)
    {
        try
        {
            if (on) LazyHttp.Value?.Add(host);
            else LazyHttp.Value?.Remove(host);
        }
        catch (Exception) { }
    }

    // «Esquecer este site ao fechar»: sites (domínio registável) cujos dados se apagam ao fechar a última aba.
    private static readonly Lazy<StrictSiteStore?> LazyForget = new(() =>
    {
        try { return new StrictSiteStore(AppPaths.Database, "forget_sites", siteOf: SiteScope.OfHost); }
        catch (Exception) { return null; }
    });

    public static bool IsForgetOnClose(string host) => LazyForget.Value?.Contains(host) == true;

    public static IReadOnlyList<string> ForgetOnCloseSites() => LazyForget.Value?.All() ?? [];

    public static void SetForgetOnClose(string host, bool on)
    {
        try
        {
            if (on) LazyForget.Value?.Add(host);
            else LazyForget.Value?.Remove(host);
        }
        catch (Exception) { }
    }

    // As permissões dadas ao site e aos seus subdomínios, com qualquer porta (meet.google.com em google.com,
    // app.exemplo.pt:8443 em exemplo.pt), para «Triturar» e «Esquecer ao fechar».
    public static void ForgetSitePermissions(string site)
    {
        try { LazyPermissions.Value?.ForgetWhere(key => SiteScope.HostKeyBelongs(key, site)); }
        catch (Exception) { }
    }

    // Sites que abrem no motor de vídeo (precisam de H.264/AAC). Duas listas: a do motor principal sem codecs, e a do
    // motor com codecs da plataforma (Tabs/PlatformCodecs.cs) — os sites lembrados só por faltarem os codecs não devem
    // continuar a sair do motor principal quando ele os passa a ter; desligar um site tira-o das duas.
    private static readonly Lazy<StrictSiteStore?> LazyVideo = new(() =>
    {
        try { return new StrictSiteStore(AppPaths.Database, "video_sites"); }
        catch (Exception) { return null; }
    });

    private static readonly Lazy<StrictSiteStore?> LazyVideoWithCodecs = new(() =>
    {
        try { return new StrictSiteStore(AppPaths.Database, "video_sites_codecs"); }
        catch (Exception) { return null; }
    });

    public static bool IsVideoSite(string host, bool platformCodecs = false)
        => (platformCodecs ? LazyVideoWithCodecs : LazyVideo).Value?.Contains(host) == true;

    public static void SetVideoSite(string host, bool on, bool platformCodecs = false)
    {
        try
        {
            if (on) (platformCodecs ? LazyVideoWithCodecs : LazyVideo).Value?.Add(host);
            else
            {
                LazyVideo.Value?.Remove(host);
                LazyVideoWithCodecs.Value?.Remove(host);
            }
        }
        catch (Exception) { }
    }

    // Tira e devolve a linha (para o «Anular» a repor com a data de criação).
    public static SavedBookmark? TakeBookmark(string url)
    {
        try
        {
            if (LazyBookmarks.Value.Saved(url) is not { } saved) return null;
            LazyBookmarks.Value.Remove(url);
            return saved;
        }
        catch (Exception) { return null; }
    }

    public static bool RestoreBookmark(SavedBookmark saved)
    {
        try { return LazyBookmarks.Value.Restore(saved); }
        catch (Exception) { return false; }
    }

    // Sites que o utilizador mandou voltar ao motor principal (botão «Motor de vídeo»): nunca trocam sozinhos, nem os da
    // lista embutida (VideoEngine.KnownH264Sites).
    private static readonly Lazy<StrictSiteStore?> LazyVideoOff = new(() =>
    {
        try { return new StrictSiteStore(AppPaths.Database, "video_sites_off"); }
        catch (Exception) { return null; }
    });

    public static bool IsVideoOptOut(string host) => LazyVideoOff.Value?.Contains(host) == true;

    public static void SetVideoOptOut(string host, bool on)
    {
        try
        {
            if (on) LazyVideoOff.Value?.Add(host);
            else LazyVideoOff.Value?.Remove(host);
        }
        catch (Exception) { }
    }

    public static void RemoveBookmark(string url)
    {
        try { LazyBookmarks.Value.Remove(url); }
        catch (Exception) { }
    }

    public static void RenameBookmark(string url, string title)
    {
        try { LazyBookmarks.Value.Rename(url, title); }
        catch (Exception) { }
    }

    public static bool IsBookmarked(string url)
    {
        try { return LazyBookmarks.Value.Contains(url); }
        catch (Exception) { return false; }
    }

    public static bool ToggleBookmark(string url, string title)
    {
        try { return LazyBookmarks.Value.Toggle(url, title, DateTime.UtcNow); }
        catch (Exception) { return false; }
    }
}
