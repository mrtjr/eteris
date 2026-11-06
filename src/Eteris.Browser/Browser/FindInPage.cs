using CefSharp;

namespace Eteris.Browser.Browser;

public static class FindFormat
{
    public static string Count(string query, int active, int total)
        => query.Length == 0 ? string.Empty : total == 0 ? "Sem resultados" : $"{active} de {total}";
}

public sealed class FindResultHandler(Action<int, int> onResult) : CefSharp.Handler.FindHandler
{
    protected override void OnFindResult(IWebBrowser chromiumWebBrowser, IBrowser browser, int identifier, int count,
        CefSharp.Structs.Rect selectionRect, int activeMatchOrdinal, bool finalUpdate)
        => onResult(activeMatchOrdinal, count);
}
