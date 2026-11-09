using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Eteris.Browser.Vault;

// As restrições que a página pôs no campo (minlength, maxlength, pattern), vindas da mensagem «generate» do script.
public sealed record PasswordRules(int? MinLength, int? MaxLength, string? Pattern);

// O gerador de palavras-passe (docs/palavras-passe.md, C5; desenho, secção 3.8): RandomNumberGenerator, 20 carateres
// por omissão, das quatro classes, sem os que se confundem (l, I, O, 0, o, 1). Respeita minlength e maxlength e um
// subconjunto de pattern: uma sequência de classes ([…], \d, \w, ., letras) com quantificadores ({n}, {n,m}, {n,},
// +, *, ?) gera-se peça a peça; outro padrão (lookaheads, grupos, alternativas) aceita a palavra-passe normal se ela o
// cumprir. A palavra-passe inteira tem de cumprir o padrão, como no HTML; se não der, null (o gerador não se oferece).
public static class PasswordGenerator
{
    public const string Lower = "abcdefghijkmnpqrstuvwxyz";
    public const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    public const string Digits = "23456789";
    public const string Symbols = "-_.!@#$%*+=?";
    public const int DefaultLength = 20;
    public const int MaxLength = 64;

    private const string All = Lower + Upper + Digits + Symbols;
    private static readonly TimeSpan s_regexTimeout = TimeSpan.FromMilliseconds(200);

    public static double EntropyBits(int length, int alphabet) => length * Math.Log2(alphabet);

    public static string? Generate(PasswordRules? rules = null)
    {
        var min = rules?.MinLength is > 0 and var m ? m : (int?)null;
        var max = rules?.MaxLength is > 0 and var x ? x : (int?)null;
        if (min > max) return null;
        var hi = Math.Min(max ?? MaxLength, MaxLength);
        var target = Math.Clamp(DefaultLength, Math.Min(min ?? 1, hi), hi);
        if (string.IsNullOrEmpty(rules?.Pattern)) return Default(target);

        Regex check;
        try { check = new Regex("^(?:" + rules.Pattern + ")$", RegexOptions.CultureInvariant, s_regexTimeout); }
        catch (ArgumentException) { return null; }
        bool Fits(string pw) => pw.Length >= (min ?? 0) && pw.Length <= (max ?? int.MaxValue) && Matches(check, pw);

        if (Atoms(rules.Pattern) is { } atoms)
        {
            if (atoms.All(a => a.Chars.Length == 1)) return null;
            for (var attempt = 0; attempt < 50; attempt++)
                if (FromAtoms(atoms, target) is { } pw && Fits(pw)) return pw;
            return null;
        }
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var pw = attempt < 50 ? Default(target) : Default(target, withSymbols: false);
            if (Fits(pw)) return pw;
        }
        return null;
    }

    private static bool Matches(Regex check, string pw)
    {
        try { return check.IsMatch(pw); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    // Um de cada classe (com espaço para isso) e o resto do alfabeto inteiro, baralhado.
    private static string Default(int length, bool withSymbols = true)
    {
        string[] classes = withSymbols ? [Lower, Upper, Digits, Symbols] : [Lower, Upper, Digits];
        var alphabet = string.Concat(classes);
        var chars = new List<char>(length);
        if (length >= classes.Length) chars.AddRange(classes.Select(Pick));
        while (chars.Count < length) chars.Add(Pick(alphabet));
        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string([.. chars]);
    }

    private static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];

    private sealed record Atom(string Chars, int Min, int Max);

    // O padrão como uma sequência de peças simples, ou null se tiver outra coisa.
    private static List<Atom>? Atoms(string pattern)
    {
        var atoms = new List<Atom>();
        var i = 0;
        while (i < pattern.Length)
        {
            string? cls;
            var c = pattern[i];
            if (c == '[')
            {
                var end = pattern.IndexOf(']', i + 2);
                if (end < 0) return null;
                cls = pattern[i..(end + 1)];
                i = end + 1;
            }
            else if (c == '\\' && i + 1 < pattern.Length)
            {
                var e = pattern[i + 1];
                cls = e is 'd' or 'w' or 'D' or 'W' ? "\\" + e : char.IsLetterOrDigit(e) ? null : Regex.Escape(e.ToString());
                if (cls is null) return null;
                i += 2;
            }
            else if (c == '.') { cls = "."; i++; }
            else if (char.IsLetterOrDigit(c) || c is '-' or '_' or '@' or '#' or '!' or '%' or '=') { cls = Regex.Escape(c.ToString()); i++; }
            else return null;

            var (lo, hi) = (1, 1);
            if (i < pattern.Length)
            {
                switch (pattern[i])
                {
                    case '+': (lo, hi) = (1, MaxLength); i++; break;
                    case '*': (lo, hi) = (0, MaxLength); i++; break;
                    case '?': (lo, hi) = (0, 1); i++; break;
                    case '{':
                        var close = pattern.IndexOf('}', i);
                        if (close < 0) return null;
                        var parts = pattern[(i + 1)..close].Split(',');
                        if (!int.TryParse(parts[0], out lo)) return null;
                        hi = parts.Length == 1 ? lo : parts[1].Length == 0 ? MaxLength : int.TryParse(parts[1], out var h) ? h : -1;
                        if (parts.Length > 2 || hi < lo) return null;
                        i = close + 1;
                        break;
                }
            }
            if (CharsOf(cls) is not { Length: > 0 } chars) return null;
            atoms.Add(new Atom(chars, lo, Math.Min(hi, MaxLength)));
        }
        return atoms.Count > 0 ? atoms : null;
    }

    // Os carateres do alfabeto seguro que a classe aceita (ou, se nenhum, os ASCII visíveis que ela aceita).
    private static string? CharsOf(string cls)
    {
        Regex one;
        try { one = new Regex("^" + cls + "$", RegexOptions.CultureInvariant, s_regexTimeout); }
        catch (ArgumentException) { return null; }
        var safe = new string(All.Where(ch => one.IsMatch(ch.ToString())).ToArray());
        if (safe.Length > 0) return safe;
        var ascii = new StringBuilder();
        for (var ch = '!'; ch <= '~'; ch++) if (one.IsMatch(ch.ToString())) ascii.Append(ch);
        return ascii.ToString();
    }

    // Cada peça com o seu mínimo; o que falta para o comprimento pedido vai para as peças que podem crescer, por ordem.
    private static string? FromAtoms(List<Atom> atoms, int target)
    {
        var counts = atoms.Select(a => a.Min).ToArray();
        var extra = target - counts.Sum();
        for (var k = 0; k < atoms.Count && extra > 0; k++)
        {
            var grow = Math.Min(extra, atoms[k].Max - counts[k]);
            counts[k] += grow;
            extra -= grow;
        }
        if (counts.Sum() == 0) return null;
        var sb = new StringBuilder();
        for (var k = 0; k < atoms.Count; k++)
            for (var n = 0; n < counts[k]; n++) sb.Append(Pick(atoms[k].Chars));
        return sb.ToString();
    }
}

// Quando o gerador se oferece e a linha da lista (C5).
public static class GeneratorPolicy
{
    public const string Title = "Usar palavra-passe forte";
    public const int PreviewLength = 4;

    // Só numa palavra-passe nova, com «Oferecer para guardar palavras-passe» ligado e fora da janela privada (D-3).
    public static bool Offers(string? kind, bool offerEnabled, bool isPrivate) => kind == "new-password" && offerEnabled && !isPrivate;

    // A pré-visualização é parcial: os primeiros carateres e reticências (a palavra-passe inteira só vai para o campo).
    public static SuggestionRow Row(string password)
        => new("gerar", Title, (password.Length > PreviewLength ? password[..PreviewLength] : password) + "…", false, OriginMatch.Exact) { Glyph = "" };
}
