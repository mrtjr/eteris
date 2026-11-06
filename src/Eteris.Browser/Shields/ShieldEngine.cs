using System.Net.Http;

namespace Eteris.Browser.Shields;

// Motor ativo do escudo. Começa com a lista mínima em C# e troca para o adblock-rust assim que as listas carregam.
public static class ShieldEngine
{
    private static IRequestBlocker _current = TrackerBlocker.Default;

    public static IRequestBlocker Current => Volatile.Read(ref _current);

    public static AdblockEngine? Cosmetic => Current as AdblockEngine;

    public static HttpsFirst Https { get; } = new();

    private static readonly string ScriptletsPath =
        System.IO.Path.Combine(AppContext.BaseDirectory, "Shields", "resources", "scriptlets.json");

    public static Task StartAsync(FilterLists lists) => Task.Run(async () =>
    {
        var scriptlets = System.IO.File.Exists(ScriptletsPath) ? System.IO.File.ReadAllText(ScriptletsPath) : null;
        try
        {
            Swap(AdblockEngine.Create(lists.LoadAll(), scriptlets));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            return;
        }

        if (!lists.IsStale(DateTime.UtcNow)) return;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Eteris/1.0");
        if (await lists.UpdateAsync(http, CancellationToken.None))
            Swap(AdblockEngine.Create(lists.LoadAll(), scriptlets));
    });

    private static void Swap(AdblockEngine engine)
        => (Interlocked.Exchange(ref _current, engine) as AdblockEngine)?.Dispose();
}
