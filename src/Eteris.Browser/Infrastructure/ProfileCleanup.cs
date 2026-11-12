using System.IO;

namespace Eteris.Browser.Infrastructure;

// O Chromium mantém um histórico próprio (e Top Sites, preditores) dentro do perfil, à margem do histórico do Éteris.
// Com a gravação desligada nas preferências, estes ficheiros só podem ser restos: são apagados antes de o CEF arrancar.
public static class ProfileCleanup
{
    private static readonly string[] HistoryFiles =
    [
        "History", "History-journal", "Top Sites", "Top Sites-journal", "Visited Links",
        "Network Action Predictor", "Network Action Predictor-journal", "Shortcuts", "Shortcuts-journal",
    ];

    private static readonly string[] SiteDataFolders =
        ["Local Storage", "IndexedDB", "Session Storage", "Service Worker", "File System", "WebStorage", "Shared Dictionary"];

    private static readonly string[] CookieFiles = ["Cookies", "Cookies-journal"];

    // Caches da GPU no disco (ajuste e3, Infrastructure/PerfTuning.cs; lista verificada no Chromium 156). As globais, na
    // raiz dos dados (chrome_content_browser_client.cc): shaders do Skia, do compositor e do Graphite, de todas as
    // janelas, também da privada. As da partição, no perfil (gpu_disk_cache_type.cc): programas do WebGL e do WebGPU,
    // não separados por site. A GPUPersistentCache (chrome_content_browser_client.cc:5201-5206) só aparece com o e14.4 (a
    // cache persistente dos shaders, que só liga com o e3) ou com o Graphite e o e3 (Shields/EnginePerformance.cs).
    public static readonly string[] GlobalGpuCaches = ["ShaderCache", "GrShaderCache", "GraphiteDawnCache", "GPUPersistentCache"];
    public static readonly string[] PartitionGpuCaches = ["GPUCache", "DawnWebGPUCache", "DawnGraphiteCache"];

    private static volatile bool _gpuCachesAtExit;

    // Apagar as caches da GPU ao sair: depois de «Apagar dados › cache» (com o motor aberto o processo da GPU tem-nas
    // abertas) e depois de usar uma janela privada (as caches globais não são por janela).
    public static void RemoveGpuCachesAtExit() => _gpuCachesAtExit = true;

    public static bool ShouldRemoveGpuCaches(bool clearOnExit) => clearOnExit || _gpuCachesAtExit;

    // Devolve quantas pastas apagou. Com o motor fechado (App.OnExit) ou antes de ele arrancar.
    public static int RemoveGpuCaches(string profileRoot)
    {
        var removed = 0;
        foreach (var path in GlobalGpuCaches.Select(f => Path.Combine(profileRoot, f))
                     .Concat(PartitionGpuCaches.Select(f => Path.Combine(profileRoot, "Default", f))))
        {
            try
            {
                if (!Directory.Exists(path)) continue;
                Directory.Delete(path, recursive: true);
                removed++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    // Versões antigas deixavam o «component updater» do Chrome descarregar o módulo Widevine (DRM) da Google.
    // Já não é descarregado; apaga-se o que tiver ficado no perfil. Devolve true se havia alguma coisa.
    public static bool RemoveDownloadedWidevine(string profileRoot)
    {
        var dir = Path.Combine(profileRoot, "WidevineCdm");
        if (!Directory.Exists(dir)) return false;
        try
        {
            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    // «Apagar cookies e dados de sites ao fechar»: corre ao sair (App.OnExit, depois de o motor fechar, que tem estas
    // pastas abertas enquanto vive) e outra vez no arranque seguinte (CefConfigurator.Prepare), como garantia mesmo que o
    // fecho tenha falhado. Devolve o que ficou por apagar: o nome de cada pasta de dados de sites e «Network/Cookies» (e o
    // journal) que continuem a existir depois da tentativa; vazio quando saiu tudo (ou quando não há perfil).
    public static IReadOnlyList<string> RemoveSiteData(string profileRoot)
    {
        var stayed = new List<string>();
        var dir = Path.Combine(profileRoot, "Default");
        if (!Directory.Exists(dir)) return stayed;
        foreach (var folder in SiteDataFolders)
        {
            var path = Path.Combine(dir, folder);
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (Directory.Exists(path)) stayed.Add(folder);
        }
        foreach (var file in CookieFiles)
        {
            var path = Path.Combine(dir, "Network", file);
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (File.Exists(path)) stayed.Add("Network/" + file);
        }
        return stayed;
    }

    // «Apagar cookies e dados de sites ao fechar» no motor de vídeo: a pasta de dados do WebView2 inteira (cookies,
    // armazenamento, cache e o histórico próprio do WebView2), ao sair e no arranque seguinte, sempre com o WebView2
    // fechado. Devolve true se a pasta já não existe (ou nunca existiu); false se ficou (um processo do WebView2 ainda a
    // fechar segura ficheiros: o arranque seguinte acaba o trabalho).
    public static bool RemoveVideoEngineData(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return !Directory.Exists(folder);
    }

    // Ao sair, os processos do WebView2 fecham depois das janelas e podem ainda segurar ficheiros da pasta: tenta-se
    // algumas vezes, com pausas curtas (no máximo 8 × 250 ms = 2 s, e só com «Apagar ao fechar»). Se mesmo assim ficar,
    // o arranque seguinte (CefConfigurator.Prepare) acaba o trabalho.
    public const int VideoEngineExitAttempts = 8;
    public static readonly TimeSpan VideoEngineExitPause = TimeSpan.FromMilliseconds(250);

    // Corre a tentativa até ela dar true, no máximo `times` vezes, com uma pausa entre elas. Devolve o último resultado.
    public static bool Retry(Func<bool> attempt, int times, TimeSpan pause)
    {
        for (var i = 1; ; i++)
        {
            if (attempt()) return true;
            if (i >= times) return false;
            if (pause > TimeSpan.Zero) Thread.Sleep(pause);
        }
    }

    public static int RemoveChromiumHistory(string profileRoot)
    {
        var dir = Path.Combine(profileRoot, "Default");
        if (!Directory.Exists(dir)) return 0;
        var removed = 0;
        foreach (var name in HistoryFiles)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            try
            {
                File.Delete(path);
                removed++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }
}
