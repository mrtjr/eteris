using System.IO;
using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Sync;

// O registo local da sincronização (<perfil>\sync\sync.log, ao lado do sync.state): uma linha por falha que a sincronização
// não consegue mostrar de outra forma (a hora, um texto fixo e o tipo da exceção). Nunca leva dados, nomes de ficheiros nem
// caminhos do perfil (Clean) e nunca a mensagem de uma exceção; fica pequeno (acima de 64 KiB guarda só os últimos 32 KiB) e
// sai em qualquer «Sair deste computador» (com ou sem «apagar a chave»: SyncService.SignOut). Nunca lança: um registo que não se escreve não pode ser mais um motivo de falha.
internal static class SyncLog
{
    private const int MaxBytes = 64 * 1024;
    private const int KeepBytes = 32 * 1024;
    private const int MaxLine = 200;

    private static readonly Lock Gate = new();

    public static string FilePath => Path.Combine(AppPaths.Root, "sync", "sync.log");

    public static void Write(string text)
    {
        try
        {
            var line = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture)
                       + " " + Clean(text, AppPaths.Root, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) + Environment.NewLine;
            lock (Gate)
            {
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, line);
                Trim(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static void Delete()
    {
        try
        {
            lock (Gate) File.Delete(FilePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // Troca a raiz do perfil por «<perfil>» e a pasta do utilizador por «<utilizador>» (sem distinguir maiúsculas; um
    // argumento vazio não troca nada), tira as quebras de linha e corta a 200 caracteres.
    internal static string Clean(string text, string root, string userProfile)
    {
        // A raiz do perfil primeiro: normalmente está dentro da pasta do utilizador.
        if (root.Length > 0) text = text.Replace(root, "<perfil>", StringComparison.OrdinalIgnoreCase);
        if (userProfile.Length > 0) text = text.Replace(userProfile, "<utilizador>", StringComparison.OrdinalIgnoreCase);
        text = text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');
        return text.Length > MaxLine ? text[..MaxLine] : text;
    }

    private static void Trim(string path)
    {
        var info = new FileInfo(path);
        if (info.Length <= MaxBytes) return;
        var bytes = File.ReadAllBytes(path);
        var tail = bytes.AsSpan(bytes.Length - KeepBytes).ToArray();
        // Recomeça numa linha inteira: descarta o pedaço de linha do início.
        var start = Array.IndexOf(tail, (byte)'\n') + 1;
        File.WriteAllBytes(path, tail[start..]);
    }
}
