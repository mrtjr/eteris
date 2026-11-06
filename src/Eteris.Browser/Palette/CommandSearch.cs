using System.Globalization;
using System.Text;

namespace Eteris.Browser.Palette;

public static class CommandSearch
{
    // Pesquisa difusa por subsequência, sem maiúsculas nem acentos ("historico" encontra "Histórico").
    // 0 = sem correspondência; quanto maior, melhor.
    public static int Score(string query, string text)
    {
        var q = Normalize(query).Replace(" ", string.Empty);
        if (q.Length == 0) return 1;
        var t = Normalize(text);

        var best = 0;
        for (var start = t.IndexOf(q[0]); start >= 0; start = t.IndexOf(q[0], start + 1))
            best = Math.Max(best, ScoreFrom(q, t, start));
        return best;
    }

    public static List<T> Rank<T>(string query, IEnumerable<T> items, Func<T, string> text)
        => items
            .Select(item => (Item: item, Score: Score(query, text(item))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Item)
            .ToList();

    // O título pesa o dobro; correspondências demasiado dispersas (sem bónus nenhum) são descartadas.
    public static List<T> RankTitled<T>(string query, IEnumerable<T> items, Func<T, string> title, Func<T, string> subtitle)
    {
        var minimum = Normalize(query).Replace(" ", string.Empty).Length * 2;
        return items
            .Select(item => (Item: item, Score: Math.Max(2 * Score(query, title(item)), Score(query, subtitle(item)))))
            .Where(x => x.Score > 0 && x.Score >= minimum)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Item)
            .ToList();
    }

    private static int ScoreFrom(string q, string t, int start)
    {
        var score = 0;
        var previous = -1;
        var i = start;
        foreach (var c in q)
        {
            while (i < t.Length && t[i] != c) i++;
            if (i == t.Length) return 0;

            score += 1;
            if (i == 0) score += 3;
            if (i == 0 || !char.IsLetterOrDigit(t[i - 1])) score += 2;
            if (previous >= 0) score += i == previous + 1 ? 4 : -Math.Min(i - previous - 1, 3);
            previous = i;
            i++;
        }
        return Math.Max(score, 1);
    }

    private static string Normalize(string value)
    {
        var decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                builder.Append(c);
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
