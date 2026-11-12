using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Eteris.Browser.Data;

// A base de dados do perfil (eteris.db). Uma base danificada deixava favoritos e histórico vazios sem explicação, para
// toda a sessão; agora só a que o SQLite diz estar danificada (e nunca uma ocupada ou sem permissão) se põe de lado,
// com o nome à vista, e começa uma nova (a especificação do navegador já o prometia).
public static class ProfileDatabase
{
    // Uma falha ao abrir que não seja de corrupção (ocupada, sem acesso) repete-se passado este tempo.
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(30);

    // Levantado com o caminho da cópia quando a base se pôs de lado (a janela avisa).
    public static event Action<string>? SetAside;

    // SQLITE_CORRUPT (11) e SQLITE_NOTADB (26). Nunca SQLITE_BUSY (5) nem SQLITE_CANTOPEN (14).
    public static bool IsCorrupt(Exception e) => e is SqliteException { SqliteErrorCode: 11 or 26 };

    // Move a base (e o -wal e o -shm que existirem) para <base>.bak-<aaaammdd-hhmmss> e devolve o caminho da cópia; null se
    // não há ficheiro ou não se deixa mover (outra ligação ainda aberta): nada muda.
    public static string? MoveAside(string path, DateTime now)
    {
        if (!File.Exists(path)) return null;
        SqliteConnection.ClearAllPools();
        var copy = path + ".bak-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        try { File.Move(path, copy, overwrite: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            try
            {
                if (File.Exists(path + suffix)) File.Move(path + suffix, copy + suffix, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        SetAside?.Invoke(copy);
        return copy;
    }
}
