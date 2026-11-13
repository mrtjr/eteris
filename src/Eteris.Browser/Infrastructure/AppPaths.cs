using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Eteris.Browser.Infrastructure;

public static class AppPaths
{
    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Eteris");

    public static string Root { get; private set; } = Normalize(DefaultRoot);

    // «Perfil alternativo» (--user-data-dir): as portas «só num perfil de teste» (interruptores de teste, proxy, porta de
    // depuração) e o nome da instância. Compara as formas normalizadas (INF-06): a mesma pasta com barra final, noutras
    // maiúsculas ou pelo nome curto 8.3 não passa por outro perfil.
    public static bool IsCustomProfile => !string.Equals(Root, Normalize(DefaultRoot), StringComparison.OrdinalIgnoreCase);

    // O caminho completo, sem a barra final (só cálculo, sem tocar no disco).
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    // Expande os nomes curtos 8.3 (UTILIZ~1) de um caminho que existe; o que não existe, ou o que falha, fica como veio. Só o
    // UseRoot o chama (com --user-data-dir): um arranque normal não faz nada de novo.
    public static string LongForm(string path)
    {
        try
        {
            var buffer = new StringBuilder(1024);
            var n = GetLongPathNameW(path, buffer, buffer.Capacity);
            return n > 0 && n < buffer.Capacity ? buffer.ToString() : path;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return path;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetLongPathNameW(string path, StringBuilder buffer, int length);

    public static string Database => Path.Combine(Root, "eteris.db");

    public static string Profile => Path.Combine(Root, "profile");

    public static string Filters => Path.Combine(Root, "filters");

    // A pasta de dados do motor de vídeo (WebView2). Só existe depois de uma aba ter aberto nesse motor: sem ela,
    // nenhuma limpeza arranca o runtime (MainWindow.VideoEngine.cs, WithVideoProfileAsync).
    public static string VideoEngine => Path.Combine(Root, "video-engine");

    // Só no arranque, antes de qualquer leitura (--user-data-dir).
    public static void UseRoot(string root) => Root = LongForm(Normalize(root));

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Profile);
        Directory.CreateDirectory(Filters);
    }
}
