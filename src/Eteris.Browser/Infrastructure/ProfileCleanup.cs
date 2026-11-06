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

    // «Apagar cookies e dados de sites ao fechar»: garantia no arranque seguinte, mesmo que o fecho tenha falhado.
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
