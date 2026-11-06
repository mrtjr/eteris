using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

public class ScreenshotNamesTests
{
    private static readonly DateTime When = new(2026, 11, 5, 23, 59, 10);

    [Fact]
    public void Name_UsesSiteAndLocalTime()
        => Assert.Equal("publico.pt 2026-11-05 23.59.10.png", ScreenshotNames.For("https://www.publico.pt/artigo?x=1", When));

    [Fact]
    public void InternalPages_UseEteris()
        => Assert.Equal("eteris 2026-11-05 23.59.10.png", ScreenshotNames.For("eteris://history/", When));

    [Fact]
    public void InvalidCharacters_AreRemoved()
        => Assert.DoesNotContain(':', ScreenshotNames.For("http://localhost:8765/", When));

    [Theory]
    [InlineData(800, 20000, 800, 16384)]
    [InlineData(1200, 3000, 1200, 3000)]
    public void FullPageHeight_IsCappedForChromium(double width, double height, double w, double h)
        => Assert.Equal((w, h), ScreenshotNames.ClampSize(width, height));
}
