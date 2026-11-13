namespace Eteris.Browser.Browser;

// «Queria dizer publico.pt?» da página de erro (proposta 3, arrumações): o site conhecido (histórico e favoritos) mais
// perto de um nome que não resolve, contado em edições de letras, sem rede nem dicionário. Função pura.
public static class Typos
{
    public const int MaxDistance = 2;

    // O candidato a MaxDistance edições ou menos do nome (sem «www.», sem diferença de maiúsculas); o mais perto ganha e,
    // num empate, o primeiro da lista. Nulo quando nenhum está perto ou quando o nome já é um deles.
    public static string? Closest(string host, IEnumerable<string> candidates)
    {
        var name = Normal(host);
        if (name.Length == 0) return null;
        string? best = null;
        var bestDistance = MaxDistance + 1;
        foreach (var candidate in candidates)
        {
            var known = Normal(candidate);
            if (known.Length == 0) continue;
            if (known == name) return null;
            var distance = Distance(name, known);
            if (distance < bestDistance) (best, bestDistance) = (known, distance);
        }
        return best;
    }

    // Distância de edição (Levenshtein): inserções, remoções e trocas de uma letra.
    public static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    public static string Normal(string host)
    {
        var name = host.Trim().TrimEnd('.').ToLowerInvariant();
        return name.StartsWith("www.", StringComparison.Ordinal) ? name[4..] : name;
    }
}
