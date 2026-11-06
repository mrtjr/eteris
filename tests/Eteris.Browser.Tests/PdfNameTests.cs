using Eteris.Browser.Downloads;

namespace Eteris.Browser.Tests;

public class PdfNameTests
{
    [Theory]
    [InlineData("Fado – Wikipédia, a enciclopédia livre", "https://pt.wikipedia.org/wiki/Fado", "Fado – Wikipédia, a enciclopédia livre.pdf")]
    [InlineData("a/b:c*d?", "https://x.pt/", "a b c d.pdf")]
    [InlineData("", "https://www.publico.pt/x", "publico.pt.pdf")]
    public void PdfName_FromTitleOrHost(string title, string url, string expected)
        => Assert.Equal(expected, PdfName.For(title, url));

    [Fact]
    public void PdfName_IsShortened()
        => Assert.True(PdfName.For(new string('x', 300), "https://a.pt/").Length <= 124);
}
