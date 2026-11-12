using System.IO;
using Eteris.Browser.Infrastructure;
using Eteris.Sync.Engine;
using Eteris.Sync.Model;

namespace Eteris.Browser.Sync;

// Coleções cujo perfil local perdeu o que tinha (a base de dados danificada e posta de lado, o settings.json que não se
// leu): comparado com a base da sincronização, o perfil novo parecia «apagado» e as remoções (ou as predefinições) iam
// para a conta e para os outros aparelhos. Ficam marcadas em <perfil>\sync-rejoin.txt (uma por linha):
//  - enquanto estiverem marcadas, a leitura do perfil não as dá como apagadas (BrowserProfile: entram em «não lidas»);
//  - quando o agente da sincronização arranca, as que estão ligadas voltam a fazer a «primeira junção» (o que só existe
//    aqui sobe; com a mesma chave vale o da conta) e a marca sai.
internal static class ProfileRejoin
{
    private static string FilePath => Path.Combine(AppPaths.Root, "sync-rejoin.txt");

    public static IReadOnlySet<string> Pending() => Pending(FilePath);

    public static void Add(string collection) => Add(FilePath, collection);

    public static IReadOnlySet<string> Pending(string path)
    {
        try
        {
            if (!File.Exists(path)) return new HashSet<string>(StringComparer.Ordinal);
            return File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    public static void Add(string path, string collection)
    {
        try
        {
            var all = Pending(path).ToHashSet(StringComparer.Ordinal);
            if (!all.Add(collection)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Join('\n', all.Order(StringComparer.Ordinal)) + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static void Clear(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // Só se volta a juntar o que está ligado neste computador: juntar não liga nada.
    public static IReadOnlyList<string> ToRejoin(IReadOnlySet<string> pending, IReadOnlySet<string> enabled)
        => pending.Where(enabled.Contains).Order(StringComparer.Ordinal).ToList();

    // No arranque do agente (SyncService.Agent). Sem sincronização ligada, só se tira a marca: uma ligação futura já é uma
    // primeira junção.
    public static void Apply(SyncAgent agent)
    {
        try
        {
            var pending = Pending();
            if (pending.Count == 0) return;
            if (agent.Status.Phase != SyncPhase.Off && ToRejoin(pending, agent.EnabledCollections) is { Count: > 0 } collections)
                agent.SetCollections(collections, on: true);
            Clear(FilePath);
        }
        catch (Exception) { }
    }
}
