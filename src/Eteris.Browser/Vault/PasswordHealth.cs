using System.Security.Cryptography;
using System.Text;

namespace Eteris.Browser.Vault;

// O resultado da verificação local: só IDs (nunca as palavras-passe). Reused: grupos de itens com a mesma palavra-passe.
public sealed record HealthReport(IReadOnlyList<string> Weak, IReadOnlyList<IReadOnlyList<string>> Reused, int Checked)
{
    public int ReusedCount => Reused.Sum(g => g.Count);

    // «1 palavra-passe fraca · 2 repetidas».
    public string Summary
    {
        get
        {
            if (Weak.Count == 0 && ReusedCount == 0) return "Nenhuma palavra-passe fraca nem repetida";
            var weak = Weak.Count == 0 ? null : Weak.Count == 1 ? "1 palavra-passe fraca" : $"{Weak.Count} palavras-passe fracas";
            var reused = ReusedCount == 0 ? null : weak is null ? $"{ReusedCount} palavras-passe repetidas" : $"{ReusedCount} repetidas";
            return string.Join(" · ", new[] { weak, reused }.OfType<string>());
        }
    }

    public override string ToString() => $"HealthReport {Weak.Count} fracas, {ReusedCount} repetidas em {Checked}";
}

// Verificação local (docs/palavras-passe.md, B2): fracas e repetidas, calculadas neste computador ao abrir a secção,
// sem nenhum pedido de rede (a verificação de fugas fica para depois, D-4). Fraca é o que se adivinha depressa: curta,
// das mais comuns (com ou sem números no fim), uma sequência do teclado ou do alfabeto, um só carácter repetido, ou
// menos de 12 carateres de uma só classe.
public static class PasswordHealth
{
    public const int MinLength = 8;

    // As mais usadas em Portugal e no Brasil e as de sempre (listas públicas de senhas comuns), já sem números no fim.
    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "password", "passw0rd", "senha", "palavrapasse", "palavra-passe", "qwerty", "qwertyuiop", "azerty", "asdfgh", "asdfghjkl",
        "abc", "abcd", "abcde", "abcdef", "admin", "administrador", "root", "login", "welcome", "bemvindo", "iloveyou",
        "teamo", "amor", "amoreterno", "princesa", "dragon", "monkey", "football", "futebol", "letmein", "master", "secret",
        "segredo", "trustno1", "sunshine", "shadow", "superman", "batman", "pokemon", "starwars", "mudar", "mudar123",
        "benfica", "sporting", "porto", "fcporto", "flamengo", "corinthians", "palmeiras", "saopaulo", "gremio", "vasco",
        "santos", "cruzeiro", "brasil", "portugal", "jesus", "deus", "familia", "felicidade", "estrela", "teste", "test",
    };

    private static readonly string[] Runs = ["abcdefghijklmnopqrstuvwxyz", "0123456789", "qwertyuiop", "asdfghjkl", "zxcvbnm", "1qaz2wsx3edc"];

    public static bool IsWeak(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength) return true;
        var lower = password.ToLowerInvariant();
        var stem = lower.TrimEnd("0123456789!@#$%&*.,;:-_?".ToCharArray());
        if (stem.Length == 0 || Common.Contains(stem) || Common.Contains(lower)) return true;
        if (lower.Distinct().Count() <= 2) return true;
        foreach (var run in Runs)
            if (run.Contains(lower, StringComparison.Ordinal) || Reverse(run).Contains(lower, StringComparison.Ordinal)) return true;
        var classes = new[] { password.Any(char.IsLower), password.Any(char.IsUpper), password.Any(char.IsDigit), password.Any(c => !char.IsLetterOrDigit(c)) }.Count(x => x);
        return password.Length < 12 && classes <= 1;
    }

    // Repetidas por hash em memória (SHA-256), sem guardar as palavras-passe; os IDs pela ordem de entrada.
    public static HealthReport Check(IEnumerable<(string Id, string Password)> items)
    {
        var weak = new List<string>();
        var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var count = 0;
        foreach (var (id, password) in items)
        {
            count++;
            if (IsWeak(password)) weak.Add(id);
            if (string.IsNullOrEmpty(password)) continue;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
            if (!groups.TryGetValue(hash, out var list)) groups[hash] = list = [];
            list.Add(id);
        }
        return new(weak, groups.Values.Where(g => g.Count > 1).Select(g => (IReadOnlyList<string>)g).ToList(), count);
    }

    private static string Reverse(string text)
    {
        var chars = text.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }
}
