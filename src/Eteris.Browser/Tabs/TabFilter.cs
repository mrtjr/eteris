using System.Globalization;
using System.Text;

namespace Eteris.Browser.Tabs;

// Filtro das abas na barra lateral: todas as palavras têm de aparecer no título ou no endereço, sem olhar a acentos.
public static class TabFilter
{
    public static bool Matches(string title, string address, string query)
    {
        var haystack = Fold(title + " " + address);
        return Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries).All(haystack.Contains);
    }

    private static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(char.ToLowerInvariant(c));
        return builder.ToString();
    }
}
