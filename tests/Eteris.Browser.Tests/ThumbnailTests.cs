using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

public class ThumbnailTests
{
    [Theory]
    [InlineData(1280, 0.25)]
    [InlineData(2560, 0.125)]
    [InlineData(200, 1)]
    public void Scale_TargetsAboutThreeHundredTwentyPixels(double viewWidth, double scale)
        => Assert.Equal(scale, Thumbnails.ScaleFor(viewWidth), 3);

    [Theory]
    [InlineData("https://a.pt/", true)]
    [InlineData("eteris://newtab/", false)]
    [InlineData("eteris://error/?code=-105", false)]
    public void OnlyWebPagesGetThumbnails(string url, bool expected)
        => Assert.Equal(expected, Thumbnails.ShouldCapture(url));
}
