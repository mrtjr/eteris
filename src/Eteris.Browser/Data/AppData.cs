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
        if (pending == 0) return;
        try { LazyDaily.Value?.Add(DateTime.Today, pending); }
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
        try { write(History); }
        catch (Exception) { }
    }

    public static IReadOnlyList<T> TryRead<T>(Func<HistoryStore, IReadOnlyList<T>> read)
    {
        try { return read(History); }
        catch (Exception) { return []; }
    }

    public static IReadOnlyDictionary<string, string> SiteIcons(IEnumerable<string> urls)
    {
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

    private static readonly Lazy<SavedSessionsStore?> LazySessions = new(() =>
    {
        try { return new SavedSessionsStore(AppPaths.Database); }
        catch (Exception) { return null; }
    });

    public static IReadOnlyList<SavedSession> SavedSessions()
    {
        try { return LazySessions.Value?.All() ?? []; }
        catch (Exception) { return []; }
    }

    public static void SaveSession(string name, IReadOnlyList<SavedTab> tabs)
    {
        try { LazySessions.Value?.Save(name, tabs, DateTime.UtcNow); }
        catch (Exception) { }
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
                LazyDark.Value?.All() ?? []);
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
        }
        SetShield(site, true);
        SetDark(site, false);
        SetStrict(site, false);
        SetVideoSite(site, false);
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

    // Sites que abrem no motor de vídeo (precisam de H.264/AAC).
    private static readonly Lazy<StrictSiteStore?> LazyVideo = new(() =>
    {
        try { return new StrictSiteStore(AppPaths.Database, "video_sites"); }
        catch (Exception) { return null; }
    });

    public static bool IsVideoSite(string host) => LazyVideo.Value?.Contains(host) == true;

    public static void SetVideoSite(string host, bool on)
    {
        try
        {
            if (on) LazyVideo.Value?.Add(host);
            else LazyVideo.Value?.Remove(host);
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
