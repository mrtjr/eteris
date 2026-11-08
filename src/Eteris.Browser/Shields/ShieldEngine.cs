using System.Net.Http;
using CefSharp;

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

    // Concluída quando o motor das listas entra em uso pela 1.ª vez, ou quando se sabe que não vai haver motor (a DLL
    // falta, as listas não carregam). Até lá, Current é só a lista mínima em C#.
    private static readonly TaskCompletionSource FirstEngine = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static int _started;

    public static Task Loaded => FirstEngine.Task;

    // Teto da espera do pedido da página principal pelo motor (HoldUntilLoaded). Lido da cache, o motor chega ~150 ms
    // antes do 1.º pedido; compilado (1.º arranque depois de uma atualização) ou com a máquina carregada pode chegar
    // depois, e a página carregava sem as listas. Passado o teto, a página segue com a lista mínima, como antes.
    public static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(250);

    // A versão que entra no nome do motor compilado (FilterLists.EngineCachePath): a do Éteris e a da eteris_shields.dll
    // (um adblock-rust novo pode ter outro formato).
    internal static string EngineVersion()
    {
        var dll = new System.IO.FileInfo(System.IO.Path.Combine(AppContext.BaseDirectory, "eteris_shields.dll"));
        var app = typeof(ShieldEngine).Assembly.GetName().Version?.ToString() ?? "0";
        return dll.Exists ? $"{app}|{dll.Length}|{dll.LastWriteTimeUtc.Ticks}" : app;
    }

    // O motor das listas: do ficheiro já compilado no perfil quando as listas e a versão são as mesmas (~10× mais rápido
    // do que compilar), e sem o texto das listas passar pelo .NET. «Persist» grava-o no perfil (~10 MB) e corre depois
    // de o motor entrar em uso, fora do caminho da 1.ª página. Só um motor compilado de todas as listas se grava: um com
    // alguma lista que não se leu (bloqueada pelo antivírus, erro passageiro) serve só nesta sessão.
    internal static (AdblockEngine Engine, Action Persist) Load(FilterLists lists)
    {
        var cache = lists.EngineCachePath(EngineVersion());
        var engine = AdblockEngine.CreateFromFiles(lists.Paths(trusted: false), lists.Paths(trusted: true),
            System.IO.File.Exists(ScriptletsPath) ? ScriptletsPath : null, cache, out var origin);
        Infrastructure.StartupTrace.Mark("escudo.motor", "origem=" + origin);
        return (engine, () =>
        {
            if (origin != EngineOrigin.Compiled) return;
            var saved = engine.SaveCache(cache);
            lists.RemoveOtherEngineCaches(cache);
            Infrastructure.StartupTrace.Mark("escudo.gravado", "ok=" + (saved ? 1 : 0));
        });
    }

    // O motor das listas, já gravado no perfil.
    public static AdblockEngine Create(FilterLists lists)
    {
        var (engine, persist) = Load(lists);
        persist();
        return engine;
    }

    public static Task StartAsync(FilterLists lists)
    {
        Interlocked.Exchange(ref _started, 1);
        return Task.Run(async () =>
        {
            Infrastructure.StartupTrace.Mark("escudo.inicio");
            try
            {
                var (engine, persist) = Load(lists);
                Swap(engine);
                FirstEngine.TrySetResult();
                Infrastructure.StartupTrace.Mark("escudo.trocado");
                persist();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
            {
                return;
            }
            finally
            {
                FirstEngine.TrySetResult();
            }

            if (!lists.IsStale(DateTime.UtcNow)) return;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Eteris/1.0");
            if (!await lists.UpdateAsync(http, CancellationToken.None)) return;
            var (updated, save) = Load(lists);
            Swap(updated);
            save();
        });
    }

    // Se o pedido da página principal espera pelo motor: só enquanto ele não chegou, numa página da rede com o escudo
    // ligado (as páginas internas e os sites sem escudo seguem logo).
    internal static bool ShouldHold(bool engineMissing, string url, Func<string, bool> isProtected)
        => engineMissing && Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) && isProtected(uri.Host);

    // No arranque, o pedido da página principal que chegue antes do motor das listas espera por ele, no máximo MaxWait
    // (IRequestCallback: a thread de rede não para). Os sub-recursos só saem depois da resposta do documento, por isso
    // já encontram o motor. Continue: o pedido segue já.
    internal static CefReturnValue HoldUntilLoaded(string url, Func<string, bool> isProtected, IRequestCallback callback)
    {
        if (!ShouldHold(Volatile.Read(ref _started) == 1 && !Loaded.IsCompleted, url, isProtected)) return CefReturnValue.Continue;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Task.WhenAny(Loaded, Task.Delay(MaxWait)).ContinueWith(_ =>
        {
            Infrastructure.StartupTrace.Mark("escudo.espera",
                $"ms={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} motor={(Loaded.IsCompleted ? 1 : 0)}");
            try
            {
                using (callback) callback.Continue(true);
            }
            catch (Exception) { }
        }, TaskScheduler.Default);
        return CefReturnValue.ContinueAsync;
    }

    private static void Swap(AdblockEngine engine)
        => (Interlocked.Exchange(ref _current, engine) as AdblockEngine)?.Dispose();
}
