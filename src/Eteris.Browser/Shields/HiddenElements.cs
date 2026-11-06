namespace Eteris.Browser.Shields;

// Regras CSS dos elementos escondidos pelo utilizador. Um seletor nunca pode fechar a regra nem sair do <style>.
public static class HiddenElements
{
    public static bool IsSafeSelector(string selector)
        => selector.Trim().Length is > 0 and <= 500 && selector.IndexOfAny(['{', '}', ';', '<', '\n', '\r', '@']) < 0;

    public static string Css(IEnumerable<string> selectors)
        => string.Join('\n', selectors.Where(IsSafeSelector).Select(s => $"{s} {{ display: none !important; }}"));
}
