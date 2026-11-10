using System.Globalization;
using System.Text;

namespace Eteris.Sync.Crypto;

// A lista BIP39 em português (2048 palavras de 4 a 8 letras a–z), embutida no Eteris.Sync.dll. Não é segredo. Nunca se
// altera: é a lista oficial byte a byte, e o SHA-256 está fixado num teste e em THIRD-PARTY-NOTICES.md.
// Na lista as 4 primeiras letras identificam a palavra, por isso uma palavra pode ser dada pelas 4 primeiras letras.
internal static class Bip39Words
{
    public const int Count = 2048;
    public const string ResourceName = "Eteris.Sync.bip39-pt.txt";
    public const int Unknown = -1;
    public const int TooShort = -2;
    private const int MinPrefix = 4;

    private sealed record Table(string[] Words, Dictionary<string, int> Index, string[] Sorted);

    private static readonly Lazy<Table> Loaded = new(Load);

    // Pela ordem do ficheiro (a posição é o número de 11 bits do BIP39).
    public static IReadOnlyList<string> All => Loaded.Value.Words;

    public static string At(int index) => Loaded.Value.Words[index];

    // A palavra exata (já dobrada); -1 se não existir.
    public static int IndexOf(string folded) => Loaded.Value.Index.TryGetValue(folded, out var i) ? i : Unknown;

    // As palavras que começam pelo prefixo (já dobrado), por ordem ordinal, no máximo «max».
    public static IReadOnlyList<string> StartingWith(string folded, int max)
    {
        var sorted = Loaded.Value.Sorted;
        var result = new List<string>();
        if (max <= 0) return result;
        var lo = 0;
        var hi = sorted.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (string.CompareOrdinal(sorted[mid], folded) < 0) lo = mid + 1;
            else hi = mid;
        }
        for (var i = lo; i < sorted.Length && result.Count < max && sorted[i].StartsWith(folded, StringComparison.Ordinal); i++)
            result.Add(sorted[i]);
        return result;
    }

    // A palavra que a ficha (já dobrada) designa: a própria, ou a única que começa por ela se a ficha tiver 4 letras ou
    // mais. Unknown se nenhuma serve; TooShort se a ficha tem menos de 4 letras e não é uma palavra inteira (ou se, o
    // que a lista não permite, mais de uma palavra começa por ela).
    public static int Resolve(string folded)
    {
        var exact = IndexOf(folded);
        if (exact >= 0) return exact;
        if (folded.Length < MinPrefix) return TooShort;
        var matches = StartingWith(folded, 2);
        return matches.Count switch
        {
            0 => Unknown,
            1 => IndexOf(matches[0]),
            _ => TooShort,
        };
    }

    // Minúsculas e sem acentos (á→a, ç→c): o que o utilizador escreve fica comparável com a lista, que já é só a–z.
    // Texto com Unicode inválido (um substituto solto) não se decompõe: fica só em minúsculas, e quem o lê recusa-o.
    public static string Fold(string text)
    {
        var lower = text.ToLowerInvariant();
        string decomposed;
        try { decomposed = lower.Normalize(NormalizationForm.FormD); }
        catch (ArgumentException) { decomposed = lower; }
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString();
    }

    // Os bytes do recurso, tal como foram embutidos (para o teste do SHA-256).
    internal static byte[] ReadResource()
    {
        using var stream = typeof(Bip39Words).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("A lista de palavras do kit de recuperação não está embutida.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    // Um recurso em falta ou com outro número de palavras é um defeito da compilação, não um erro do utilizador.
    private static Table Load()
    {
        var words = Encoding.UTF8.GetString(ReadResource())
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 0)
            .ToArray();
        if (words.Length != Count) throw new InvalidOperationException($"A lista de palavras do kit de recuperação tem {words.Length} palavras em vez de {Count}.");
        var index = new Dictionary<string, int>(Count, StringComparer.Ordinal);
        for (var i = 0; i < words.Length; i++) index[words[i]] = i;
        var sorted = (string[])words.Clone();
        Array.Sort(sorted, StringComparer.Ordinal);
        return new Table(words, index, sorted);
    }
}
