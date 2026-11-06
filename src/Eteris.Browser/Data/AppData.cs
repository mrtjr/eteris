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

    public static HistoryStore History => LazyHistory.Value;

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

    private static readonly Lazy<BookmarkStore> LazyBookmarks = new(() => new BookmarkStore(AppPaths.Database));

    public static IReadOnlyList<Bookmark> Bookmarks(Func<BookmarkStore, IReadOnlyList<Bookmark>> read)
    {
        try { return read(LazyBookmarks.Value); }
        catch (Exception) { return []; }
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
