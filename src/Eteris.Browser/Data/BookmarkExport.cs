using System.Text;

namespace Eteris.Browser.Data;

// Exportar favoritos no formato «Netscape Bookmark File» — o HTML que o Chrome, o Edge, o Firefox e o Safari importam.
// Os favoritos não ficam presos ao Éteris.
public static class BookmarkExport
{
    public static string ToHtml(IReadOnlyList<Bookmark> bookmarks, DateTime nowUtc)
    {
        var stamp = new DateTimeOffset(nowUtc).ToUnixTimeSeconds();
        var html = new StringBuilder();
        html.Append("<!DOCTYPE NETSCAPE-Bookmark-file-1>\n");
        html.Append("<!-- Exportado pelo Éteris. Este ficheiro é lido por qualquer navegador (Importar favoritos › ficheiro HTML). -->\n");
        html.Append("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">\n");
        html.Append("<TITLE>Bookmarks</TITLE>\n<H1>Bookmarks</H1>\n<DL><p>\n");
        html.Append($"    <DT><H3 ADD_DATE=\"{stamp}\" LAST_MODIFIED=\"{stamp}\">Éteris</H3>\n    <DL><p>\n");
        foreach (var mark in bookmarks)
        {
            if (!Uri.TryCreate(mark.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ftp")) continue;
            var title = mark.Title.Trim().Length > 0 ? mark.Title.Trim() : mark.Url;
            html.Append($"        <DT><A HREF=\"{Escape(mark.Url)}\" ADD_DATE=\"{stamp}\">{Escape(title)}</A>\n");
        }
        html.Append("    </DL><p>\n</DL><p>\n");
        return html.ToString();
    }

    // O ficheiro é UTF-8: só se escapam os caracteres com significado em HTML (acentos ficam legíveis).
    private static string Escape(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    public static string FileName(DateTime localNow) => $"Favoritos do Éteris {localNow:yyyy-MM-dd}.html";
}
