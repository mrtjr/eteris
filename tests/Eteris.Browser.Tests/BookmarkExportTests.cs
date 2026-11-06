using Eteris.Browser.Data;

namespace Eteris.Browser.Tests;

public class BookmarkExportTests
{
    private static readonly DateTime T0 = new(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ToHtml_IsANetscapeBookmarkFile()
    {
        var html = BookmarkExport.ToHtml([new Bookmark("https://pt.wikipedia.org/wiki/Fado", "Fado – Wikipédia")], T0);
        Assert.StartsWith("<!DOCTYPE NETSCAPE-Bookmark-file-1>", html);
        Assert.Contains("<DT><A HREF=\"https://pt.wikipedia.org/wiki/Fado\" ADD_DATE=\"1793955600\">Fado – Wikipédia</A>", html);
        Assert.Contains(">Éteris</H3>", html);
    }

    [Fact]
    public void ToHtml_EscapesTitlesAndAddressesAndSkipsUnsafeSchemes()
    {
        var html = BookmarkExport.ToHtml([
            new Bookmark("https://a.pt/?x=1&y=\"2\"", "<script>alert(1)</script>"),
            new Bookmark("javascript:alert(1)", "mau"),
            new Bookmark("https://b.pt/", "  "),
        ], T0);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("HREF=\"https://a.pt/?x=1&amp;y=&quot;2&quot;\"", html);
        Assert.DoesNotContain("javascript:", html);
        Assert.Contains(">https://b.pt/</A>", html);
    }

    [Fact]
    public void FileName_HasTheDate() => Assert.Equal("Favoritos do Éteris 2026-11-06.html", BookmarkExport.FileName(new DateTime(2026, 11, 6, 2, 30, 0)));
}
