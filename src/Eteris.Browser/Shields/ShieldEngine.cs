using System.Net.Http;

namespace Eteris.Browser.Shields;

// Motor ativo do escudo. Começa com a lista mínima em C# e troca para o adblock-rust assim que as listas carregam.
public static class ShieldEngine
{
    private static IRequestBlocker _current = TrackerBlocker.Default;

    public static IRequestBlocker Current => Volatile.Read(ref _current);

    public static AdblockEngine? Cosmetic => Current as AdblockEngine;

    // As exceções «Sempre para este site» (http:// sem aviso) vêm da base de dados, consultadas a cada navegação.
    public static HttpsFirst Https { get; } = new(Data.AppData.IsHttpException);

    private static readonly string ScriptletsPath =
        System.IO.Path.Combine(AppContext.BaseDirectory, "Shields", "resources", "scriptlets.json");

    // A versão que entra no nome do motor compilado (FilterLists.EngineCachePath): a do Éteris e a da eteris_shields.dll
    // (um adblock-rust novo pode ter outro formato).
    private static string EngineVersion()
    {
        var dll = new System.IO.FileInfo(System.IO.Path.Combine(AppContext.BaseDirectory, "eteris_shields.dll"));
        var app = typeof(ShieldEngine).Assembly.GetName().Version?.ToString() ?? "0";
        return dll.Exists ? $"{app}|{dll.Length}|{dll.LastWriteTimeUtc.Ticks}" : app;
    }

    // O motor das listas: do ficheiro já compilado no perfil quando as listas e a versão são as mesmas (~10× mais rápido
    // do que compilar), e sem o texto das listas passar pelo .NET.
    public static AdblockEngine Create(FilterLists lists)
    {
        var cache = lists.EngineCachePath(EngineVersion());
        var engine = AdblockEngine.CreateFromFiles(lists.Paths(trusted: false), lists.Paths(trusted: true),
            System.IO.File.Exists(ScriptletsPath) ? ScriptletsPath : null, cache, out var fromCache);
        Infrastructure.StartupTrace.Mark("escudo.motor", "cache=" + (fromCache ? 1 : 0));
        if (!fromCache) lists.RemoveOtherEngineCaches(cache);
        return engine;
    }

    public static Task StartAsync(FilterLists lists) => Task.Run(async () =>
    {
        Infrastructure.StartupTrace.Mark("escudo.inicio");
        try
        {
            Swap(Create(lists));
            Infrastructure.StartupTrace.Mark("escudo.trocado");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            return;
        }

        if (!lists.IsStale(DateTime.UtcNow)) return;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Eteris/1.0");
        if (await lists.UpdateAsync(http, CancellationToken.None)) Swap(Create(lists));
    });

    private static void Swap(AdblockEngine engine)
        => (Interlocked.Exchange(ref _current, engine) as AdblockEngine)?.Dispose();
}
