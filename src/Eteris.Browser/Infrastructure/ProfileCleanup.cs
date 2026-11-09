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

    // «Apagar cookies e dados de sites ao fechar»: garantia no arranque seguinte, mesmo que o fecho tenha falhado.
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

    public static void RemoveSiteData(string profileRoot)
    {
        var dir = Path.Combine(profileRoot, "Default");
        if (!Directory.Exists(dir)) return;
        foreach (var folder in SiteDataFolders)
        {
            try { if (Directory.Exists(Path.Combine(dir, folder))) Directory.Delete(Path.Combine(dir, folder), recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        foreach (var file in CookieFiles)
        {
            try { File.Delete(Path.Combine(dir, "Network", file)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
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
