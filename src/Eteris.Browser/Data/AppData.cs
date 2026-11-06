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
}
