using Eteris.Browser.Infrastructure;

namespace Eteris.Browser.Tests;

public class QrCodeTests
{
    [Fact]
    public void Png_IsAPngImage()
    {
        var png = QrCode.Png("https://pt.wikipedia.org/wiki/Fado");
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, png[..4]);
        Assert.True(png.Length > 200);
    }

    [Fact]
    public void LongerAddresses_NeedBiggerCodes()
        => Assert.True(QrCode.Png("https://a.pt/" + new string('x', 300)).Length > QrCode.Png("https://a.pt/").Length);

    [Theory]
    [InlineData("https://www.publico.pt/artigo", "QR publico.pt.png")]
    [InlineData("http://127.0.0.1:8765/x", "QR 127.0.0.1.png")]
    public void FileName_UsesTheSite(string url, string expected) => Assert.Equal(expected, QrCode.FileName(url));

    [Fact]
    public void Caption_DropsSchemeAndShortensInTheMiddle()
    {
        Assert.Equal("pt.wikipedia.org/wiki/Fado", QrCode.Caption("https://pt.wikipedia.org/wiki/Fado"));
        var caption = QrCode.Caption("https://example.com/" + new string('a', 80) + "/fim", 20);
        Assert.StartsWith("example.co", caption);
        Assert.EndsWith("aaaa/fim", caption);
        Assert.Contains("…", caption);
    }
}
