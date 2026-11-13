using System.Globalization;
using System.Text.RegularExpressions;

namespace Eteris.Sync.Transport;

// Onde ficam os ficheiros cifrados: uma pasta local sincronizada pela nuvem do utilizador (OneDrive, Dropbox…) ou a
// pasta oculta da aplicação no Google Drive. O transporte só vê nomes opacos e bytes cifrados; não decide nada.
// Nomes lógicos com «/» (SyncPaths); cada transporte guarda-os como lhe convém.
public interface ISyncTransport
{
    // Rótulo para a interface e o diagnóstico (nunca um caminho com o nome do utilizador).
    string Name { get; }

    // Nomes lógicos que começam por «prefix» (lista completa; a ordem não é garantida).
    Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken = default);

    // null quando não existe.
    Task<byte[]?> ReadAsync(string path, CancellationToken cancellationToken = default);

    // Cria ou substitui (só o próprio aparelho substitui os seus ficheiros mutáveis: estado e chaves).
    Task WriteAsync(string path, byte[] data, CancellationToken cancellationToken = default);

    Task DeleteAsync(string path, CancellationToken cancellationToken = default);
}

// Esquema dos nomes. Tudo debaixo de «eteris/<cadeia>/», com a cadeia = HKDF(R, "chain-id"): sem o código não se sabe
// a que conta pertence, e «nova cadeia» é só outra pasta.
//   eteris/<cadeia>/keys                    registo de chaves (DEK embrulhada)
//   eteris/<cadeia>/d/<aparelho>/<seq>      lote imutável n.º seq (12 dígitos)
//   eteris/<cadeia>/s/<aparelho>-<seq>      instantâneo feito por um aparelho até ao seu lote seq
//   eteris/<cadeia>/a/<aparelho>            estado do aparelho (o que já leu)
//   eteris/<cadeia>/r/<aparelho>/<de>       conta nova para um aparelho que fica (só numa cadeia antiga, depois de
//                                           «nova cadeia»; apagada por quem a lê)
//   eteris/pair/<sessão>/<passo>            mensagens de um emparelhamento (apagadas no fim)
public static partial class SyncPaths
{
    public const string Root = "eteris/";

    // Tamanho máximo de cada tipo de ficheiro, conferido pelo transporte antes de o pôr em memória: quem tem acesso à
    // pasta sem ter a chave (um aparelho removido) não gasta a memória de quem lê com um ficheiro enorme.
    public const int SmallFileBytes = 64 * 1024;
    public const int StateFileBytes = 1024 * 1024;

    // O máximo de um ficheiro pelo seu nome lógico: registo de chaves, mensagens r/ e passos do emparelhamento são
    // pequenos, o estado de um aparelho cabe em 1 MiB, e o resto (lotes d/, instantâneos s/, nomes desconhecidos) em
    // SealedFile.MaxFileBytes.
    public static int MaxBytes(string path)
    {
        var parts = path.Split('/');
        if (parts.Length > 1 && parts[1] == "pair") return SmallFileBytes;
        if (parts.Length == 3 && parts[2] == "keys") return SmallFileBytes;
        if (parts.Length > 2 && parts[2] == "r") return SmallFileBytes;
        if (parts.Length == 4 && parts[2] == "a") return StateFileBytes;
        return Format.SealedFile.MaxFileBytes;
    }

    public static string Chain(string chain) => $"{Root}{chain}/";
    public static string Keys(string chain) => $"{Root}{chain}/keys";
    public static string Batches(string chain) => $"{Root}{chain}/d/";
    public static string Batch(string chain, string device, long seq) => $"{Root}{chain}/d/{device}/{seq.ToString("D12", CultureInfo.InvariantCulture)}";
    public static string Snapshots(string chain) => $"{Root}{chain}/s/";
    public static string Snapshot(string chain, string device, long seq) => $"{Root}{chain}/s/{device}-{seq.ToString("D12", CultureInfo.InvariantCulture)}";
    public static string States(string chain) => $"{Root}{chain}/a/";
    public static string State(string chain, string device) => $"{Root}{chain}/a/{device}";
    public static string Pairing(string session) => $"{Root}pair/{session}/";
    public static string Rekeys(string chain, string device) => $"{Root}{chain}/r/{device}/";
    public static string Rekey(string chain, string device, string from) => $"{Root}{chain}/r/{device}/{from}";
    public static string RekeyContext(string device, string from) => $"r/{device}/{from}";

    // Cadeias que existem neste transporte (pelo registo de chaves): para saber se a conta já existe noutro aparelho.
    public static bool TryParseKeys(string path, out string chain)
    {
        var m = KeysName().Match(path);
        chain = m.Success ? m.Groups[1].Value : string.Empty;
        return m.Success;
    }

    // Contexto (AAD) de cada ficheiro: o nome lógico sem a cadeia, que já vai à parte.
    public static string BatchContext(string device, long seq) => $"d/{device}/{seq}";
    public static string SnapshotContext(string device, long seq) => $"s/{device}/{seq}";
    public static string StateContext(string device) => $"a/{device}";

    // Só nomes com esta forma contam: as «cópias em conflito» que a nuvem cria («000000000001 (1)»), temporários e
    // ficheiros alheios são ignorados.
    public static bool TryParseBatch(string chain, string path, out string device, out long seq)
    {
        var m = BatchName().Match(path);
        device = m.Success ? m.Groups[2].Value : string.Empty;
        seq = m.Success ? long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        return m.Success && m.Groups[1].Value == chain;
    }

    public static bool TryParseSnapshot(string chain, string path, out string device, out long seq)
    {
        var m = SnapshotName().Match(path);
        device = m.Success ? m.Groups[2].Value : string.Empty;
        seq = m.Success ? long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        return m.Success && m.Groups[1].Value == chain;
    }

    public static bool TryParseState(string chain, string path, out string device)
    {
        var m = StateName().Match(path);
        device = m.Success ? m.Groups[2].Value : string.Empty;
        return m.Success && m.Groups[1].Value == chain;
    }

    public static bool IsValidSegment(string segment) => SegmentName().IsMatch(segment);

    [GeneratedRegex(@"^eteris/([0-9a-f]{32})/d/([0-9a-f]{32})/(\d{12})$")]
    private static partial Regex BatchName();

    [GeneratedRegex(@"^eteris/([0-9a-f]{32})/s/([0-9a-f]{32})-(\d{12})$")]
    private static partial Regex SnapshotName();

    [GeneratedRegex(@"^eteris/([0-9a-f]{32})/a/([0-9a-f]{32})$")]
    private static partial Regex StateName();

    [GeneratedRegex(@"^eteris/([0-9a-f]{32})/keys$")]
    private static partial Regex KeysName();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SegmentName();
}
