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

    public static Task StartAsync(FilterLists lists) => Task.Run(async () =>
    {
        Infrastructure.StartupTrace.Mark("escudo.inicio");
        var scriptlets = System.IO.File.Exists(ScriptletsPath) ? System.IO.File.ReadAllText(ScriptletsPath) : null;
        try
        {
            Swap(Infrastructure.StartupTrace.IsOn ? CreateTraced(lists, scriptlets) : AdblockEngine.Create(lists.Load(trusted: false), scriptlets, lists.Load(trusted: true)));
            Infrastructure.StartupTrace.Mark("escudo.trocado");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            return;
        }

        if (!lists.IsStale(DateTime.UtcNow)) return;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Eteris/1.0");
        if (await lists.UpdateAsync(http, CancellationToken.None))
            Swap(AdblockEngine.Create(lists.Load(trusted: false), scriptlets, lists.Load(trusted: true)));
    });

    // Com ETERIS_TRACE_STARTUP: o mesmo que acima, com o tempo de ler as listas e o de compilar o motor separados.
    // Fora do método assíncrono, para os textos das listas não ficarem presos à máquina de estados durante a atualização.
    private static AdblockEngine CreateTraced(FilterLists lists, string? scriptlets)
    {
        var rules = lists.Load(trusted: false);
        var trustedRules = lists.Load(trusted: true);
        Infrastructure.StartupTrace.Mark("escudo.listas", $"regras={rules.Length} confiaveis={trustedRules.Length} scriptlets={scriptlets?.Length ?? 0}");
        var engine = AdblockEngine.Create(rules, scriptlets, trustedRules);
        Infrastructure.StartupTrace.Mark("escudo.motor");
        return engine;
    }

    private static void Swap(AdblockEngine engine)
        => (Interlocked.Exchange(ref _current, engine) as AdblockEngine)?.Dispose();
}
