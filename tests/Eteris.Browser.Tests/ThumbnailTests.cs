using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

public class ThumbnailTests
{
    private static byte[] Jpeg(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new JpegBitmapEncoder { QualityLevel = 70 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    [Theory]
    [InlineData(1280, 800, 320, 200)]
    [InlineData(2560, 1600, 320, 200)]
    public void Decode_ReducesTheFullCaptureToTheCardWidth(int width, int height, int expectedWidth, int expectedHeight)
    {
        var image = Thumbnails.Decode(Jpeg(width, height));
        Assert.Equal(expectedWidth, image.PixelWidth);
        Assert.Equal(expectedHeight, image.PixelHeight);
    }

    [Fact]
    public void Decode_NeverEnlargesASmallCapture()
        => Assert.Equal(200, Thumbnails.Decode(Jpeg(200, 120)).PixelWidth);

    [Fact]
    public void Decode_IsFrozen_SoItCanBeMadeOffTheWindowThread()
        => Assert.True(Thumbnails.Decode(Jpeg(640, 400)).IsFrozen);

    [Theory]
    [InlineData("https://a.pt/", true)]
    [InlineData("eteris://newtab/", false)]
    [InlineData("eteris://error/?code=-105", false)]
    public void OnlyWebPagesGetThumbnails(string url, bool expected)
        => Assert.Equal(expected, Thumbnails.ShouldCapture(url));
}
