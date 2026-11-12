using System.IO;

namespace Eteris.Browser.Infrastructure;

public static class AppPaths
{
    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Eteris");

    public static string Root { get; private set; } = DefaultRoot;

    public static bool IsCustomProfile => !string.Equals(Root, DefaultRoot, StringComparison.OrdinalIgnoreCase);

    public static string Database => Path.Combine(Root, "eteris.db");

    public static string Profile => Path.Combine(Root, "profile");

    public static string Filters => Path.Combine(Root, "filters");

    // A pasta de dados do motor de vídeo (WebView2). Só existe depois de uma aba ter aberto nesse motor: sem ela,
    // nenhuma limpeza arranca o runtime (MainWindow.VideoEngine.cs, WithVideoProfileAsync).
    public static string VideoEngine => Path.Combine(Root, "video-engine");

    // Só no arranque, antes de qualquer leitura (--user-data-dir).
    public static void UseRoot(string root) => Root = Path.GetFullPath(root);

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Profile);
        Directory.CreateDirectory(Filters);
    }
}
