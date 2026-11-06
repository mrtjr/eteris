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
