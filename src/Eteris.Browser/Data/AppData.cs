using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Data;

public static class AppData
{
    private static readonly Lazy<HistoryStore> LazyHistory = new(() => new HistoryStore(AppPaths.Database));

    public static HistoryStore History => LazyHistory.Value;

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
