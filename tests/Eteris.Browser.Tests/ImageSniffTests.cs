using System.IO;
using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

public class ImageSniffTests
{
    private static byte[] Png(int w, int h) => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52,
        (byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w, (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h, 1, 0, 0, 0, 0];
    private static byte[] Gif(int w, int h) => [0x47, 0x49, 0x46, 0x38, 0x39, 0x61, (byte)w, (byte)(w >> 8), (byte)h, (byte)(h >> 8), 0, 0, 0];
    private static byte[] Jpeg(int w, int h) => [0xFF, 0xD8, 0xFF, 0xE0, 0, 4, 0, 0, 0xFF, 0xC0, 0, 11, 8, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 1, 1, 0x11, 0];
    private static byte[] Ico(byte side, byte[] payload)
        => [0, 0, 1, 0, 1, 0, side, side, 0, 0, 1, 0, 32, 0, (byte)payload.Length, (byte)(payload.Length >> 8), 0, 0, 22, 0, 0, 0, .. payload];
    // Cabeçalho de um bitmap dentro de um ICO (BITMAPINFOHEADER): a altura conta a dobrar (imagem + máscara).
    private static byte[] Dib(int w, int h) => [40, 0, 0, 0, (byte)w, (byte)(w >> 8), (byte)(w >> 16), (byte)(w >> 24),
        (byte)(h * 2), (byte)(h * 2 >> 8), (byte)(h * 2 >> 16), (byte)(h * 2 >> 24), 1, 0, 32, 0];

    [Fact]
    public void OnlyPngJpegGifIco_AreAccepted()
    {
        Assert.Equal(SniffedImage.Png, ImageSniff.Kind(Png(32, 32)));
        Assert.Equal(SniffedImage.Gif, ImageSniff.Kind(Gif(32, 32)));
        Assert.Equal(SniffedImage.Jpeg, ImageSniff.Kind(Jpeg(32, 32)));
        Assert.Equal(SniffedImage.Ico, ImageSniff.Kind(Ico(32, new byte[40])));
        Assert.Null(ImageSniff.Kind("RIFF\0\0\0\0WEBPVP8 "u8));                                  // WebP
        Assert.Null(ImageSniff.Kind([0x49, 0x49, 0x2A, 0x00, 8, 0, 0, 0]));                      // TIFF
        Assert.Null(ImageSniff.Kind([0x42, 0x4D, 0, 0, 0, 0, 0, 0, 0, 0, 54, 0, 0, 0]));         // BMP
        Assert.Null(ImageSniff.Kind([0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63])); // HEIF
        Assert.Null(ImageSniff.Kind("<svg xmlns='http://www.w3.org/2000/svg'/>"u8));
        Assert.Null(ImageSniff.Kind([]));
    }

    [Fact]
    public void HugeHeaderDimensions_AreRefused()
    {
        Assert.Equal((60000, 60000), ImageSniff.Size(Png(60000, 60000)));
        Assert.False(ImageSniff.Accepts(Png(60000, 60000), ImageSniff.MaxPreviewSide));
        Assert.False(ImageSniff.Accepts(Png(513, 32), ImageSniff.MaxIconSide));
        Assert.True(ImageSniff.Accepts(Png(512, 512), ImageSniff.MaxIconSide));
        Assert.True(ImageSniff.Accepts(Jpeg(1200, 630), ImageSniff.MaxPreviewSide));
        Assert.False(ImageSniff.Accepts(Jpeg(5000, 100), ImageSniff.MaxPreviewSide));
        Assert.False(ImageSniff.Accepts(Gif(600, 10), ImageSniff.MaxIconSide));
        Assert.False(ImageSniff.Accepts(Png(0, 32), ImageSniff.MaxIconSide));
        Assert.False(ImageSniff.Accepts([0xFF, 0xD8, 0xFF, 0xD9], ImageSniff.MaxPreviewSide));   // JPEG sem quadro
        // ICO cuja entrada diz 32×32 mas traz um PNG de 60 000×60 000.
        Assert.False(ImageSniff.Accepts(Ico(32, Png(60000, 60000)), ImageSniff.MaxIconSide));
        Assert.True(ImageSniff.Accepts(Ico(32, Png(32, 32)), ImageSniff.MaxIconSide));
        // O mesmo com um bitmap embutido: o cabeçalho dele manda mais do que a entrada.
        Assert.False(ImageSniff.Accepts(Ico(32, Dib(60000, 60000)), ImageSniff.MaxIconSide));
        Assert.True(ImageSniff.Accepts(Ico(32, Dib(32, 32)), ImageSniff.MaxIconSide));
        Assert.Equal((256, 256), ImageSniff.Size(Ico(0, Dib(256, 256))));
        // Entrada que aponta para fora dos bytes.
        Assert.False(ImageSniff.Accepts([0, 0, 1, 0, 1, 0, 32, 32, 0, 0, 1, 0, 32, 0, 40, 0, 0, 0, 200, 0, 0, 0], ImageSniff.MaxIconSide));
    }

    [Fact]
    public void TruncatedAndLyingHeaders_NeverThrow()
    {
        byte[][] samples = [Png(32, 32), Gif(32, 32), Jpeg(32, 32), Ico(32, Png(32, 32)), Ico(32, Dib(32, 32))];
        foreach (var sample in samples)
            for (var length = 0; length <= sample.Length; length++)
            {
                var prefix = sample.AsSpan(0, length).ToArray();
                var ex = Record.Exception(() =>
                {
                    ImageSniff.Kind(prefix);
                    ImageSniff.Size(prefix);
                    ImageSniff.Accepts(prefix, ImageSniff.MaxPreviewSide);
                });
                Assert.Null(ex);
            }

        // Deslocamento perto de uint.MaxValue (a soma com o tamanho não pode dar a volta).
        byte[] farOffset = [0, 0, 1, 0, 1, 0, 32, 32, 0, 0, 1, 0, 32, 0, 0x10, 0, 0, 0, 0xF8, 0xFF, 0xFF, 0xFF, 1, 2, 3, 4];
        Assert.False(ImageSniff.Accepts(farOffset, ImageSniff.MaxIconSide));
        byte[] hugeSizeAndOffset = [0, 0, 1, 0, 1, 0, 32, 32, 0, 0, 1, 0, 32, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 1];
        Assert.False(ImageSniff.Accepts(hugeSizeAndOffset, ImageSniff.MaxIconSide));
        // 64 entradas declaradas, bytes só para uma.
        byte[] manyEntries = [0, 0, 1, 0, 64, 0, 16, 16, 0, 0, 1, 0, 32, 0, 0, 0, 0, 0, 22, 0, 0, 0];
        Assert.False(ImageSniff.Accepts(manyEntries, ImageSniff.MaxIconSide));
        // JPEG cujo segmento diz ter mais bytes do que há, ou menos do que os dois do próprio comprimento.
        Assert.False(ImageSniff.Accepts([0xFF, 0xD8, 0xFF, 0xE0, 0xFF, 0xFF, 0, 0, 0xFF, 0xC0, 0, 11, 8, 0, 32, 0, 32], ImageSniff.MaxPreviewSide));
        Assert.False(ImageSniff.Accepts([0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0xFF, 0xC0, 0, 11, 8, 0, 32, 0, 32], ImageSniff.MaxPreviewSide));
        Assert.False(ImageSniff.Accepts([0xFF, 0xD8, 0xFF, 0xE0, 0, 1, 0xFF, 0xC0, 0, 11, 8, 0, 32, 0, 32], ImageSniff.MaxPreviewSide));
        // Quadro que diz ter 11 bytes mas os bytes acabam antes das dimensões.
        Assert.False(ImageSniff.Accepts([0xFF, 0xD8, 0xFF, 0xC0, 0, 11, 8, 0, 32], ImageSniff.MaxPreviewSide));
    }

    [Fact]
    public void SiteImages_AreSniffedBeforeTheDecoder()
    {
        foreach (var file in new[] { "MainWindow.Favicons.cs", "MainWindow.Glance.cs" })
        {
            var source = File.ReadAllText(Path.Combine(Sync.GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", file));
            Assert.Contains("ImageSniff.Accepts(", source);
            Assert.Contains("Task.Run(", source);
        }
    }
}
