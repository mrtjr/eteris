namespace Eteris.Sync.Merge;

// Fusão a três por linhas (diff3) das notas: base comum + duas versões editadas em aparelhos diferentes.
// Linhas mudadas só de um lado entram; mudadas igual dos dois lados entram uma vez; mudadas de forma diferente nos
// dois lados ficam AMBAS, num bloco marcado, sem se perder texto. A ordem dos lados é a do chamador (o motor passa
// sempre a versão mais antiga primeiro), por isso todos os aparelhos chegam ao mesmo texto.
public static class NotesMerge
{
    // Acima disto (linhas × linhas da parte que difere) não se calcula a LCS: o bloco todo vira conflito.
    private const long MaxCells = 4_000_000;

    public sealed record Result(string Text, int Conflicts);

    public static Result Merge(string baseText, string first, string second, string firstLabel = "A", string secondLabel = "B")
    {
        if (first == second) return new(first, 0);
        if (first == baseText) return new(second, 0);
        if (second == baseText) return new(first, 0);

        var o = Lines(baseText);
        var a = Lines(first);
        var b = Lines(second);
        var ma = Match(o, a);
        var mb = Match(o, b);

        var output = new List<string>();
        var conflicts = 0;
        int io = 0, ia = 0, ib = 0;
        while (true)
        {
            // Próxima linha da base que ficou igual nos dois lados (ponto estável).
            var next = io;
            while (next < o.Length && (ma[next] < 0 || mb[next] < 0)) next++;
            var endA = next < o.Length ? ma[next] : a.Length;
            var endB = next < o.Length ? mb[next] : b.Length;

            var chunkO = o[io..next];
            var chunkA = a[ia..endA];
            var chunkB = b[ib..endB];
            if (chunkO.Length > 0 || chunkA.Length > 0 || chunkB.Length > 0)
            {
                if (chunkA.SequenceEqual(chunkO)) output.AddRange(chunkB);
                else if (chunkB.SequenceEqual(chunkO)) output.AddRange(chunkA);
                else if (chunkA.SequenceEqual(chunkB)) output.AddRange(chunkA);
                else
                {
                    conflicts++;
                    output.Add($"⚠ Conflito de sincronização: ficaram as duas versões ({firstLabel} e {secondLabel}).");
                    output.Add($"——— {firstLabel} ———");
                    output.AddRange(chunkA);
                    output.Add($"——— {secondLabel} ———");
                    output.AddRange(chunkB);
                    output.Add("——— fim do conflito ———");
                }
            }
            if (next >= o.Length) break;
            output.Add(o[next]);
            io = next + 1;
            ia = endA + 1;
            ib = endB + 1;
        }
        return new(string.Join('\n', output), conflicts);
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    // Para cada linha da base, o índice da linha igual na outra versão pela maior subsequência comum, ou -1.
    private static int[] Match(string[] o, string[] x)
    {
        var map = Enumerable.Repeat(-1, o.Length).ToArray();
        var prefix = 0;
        while (prefix < o.Length && prefix < x.Length && o[prefix] == x[prefix]) { map[prefix] = prefix; prefix++; }
        var suffix = 0;
        while (suffix < o.Length - prefix && suffix < x.Length - prefix && o[o.Length - 1 - suffix] == x[x.Length - 1 - suffix])
        {
            map[o.Length - 1 - suffix] = x.Length - 1 - suffix;
            suffix++;
        }
        int n = o.Length - prefix - suffix, m = x.Length - prefix - suffix;
        if (n == 0 || m == 0 || (long)n * m > MaxCells) return map;

        // LCS clássica sobre o meio (o que difere), com a tabela de trás para a frente para reconstruir de cima.
        var table = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                table[i, j] = o[prefix + i] == x[prefix + j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
        int p = 0, q = 0;
        while (p < n && q < m)
        {
            if (o[prefix + p] == x[prefix + q]) { map[prefix + p] = prefix + q; p++; q++; }
            else if (table[p + 1, q] >= table[p, q + 1]) p++;
            else q++;
        }
        return map;
    }
}
