using System.IO;
using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

// Os fundos da nova aba são servidos por um nome construído à mão em EterisSchemeHandlerFactory
// ("Eteris.Browser.Browser.Wallpapers." + ficheiro). Se o glob do csproj ou a pasta mudarem, a página fica só com o
// gradiente, sem erro em lado nenhum: estes testes fixam os nomes e o tipo de cada ficheiro.
public class EmbeddedResourcesTests
{
    private const string Prefix = "Eteris.Browser.Browser.Wallpapers.";

    [Theory]
    [InlineData("coast.jpg", "FFD8")]            // JPEG
    [InlineData("mountains.jpg", "FFD8")]
    [InlineData("coast-live.webm", "1A45DFA3")]  // EBML (WebM)
    public void Wallpaper_IsEmbeddedUnderTheNameTheSchemeHandlerBuilds(string file, string magicHex)
    {
        var assembly = typeof(NewTabPage).Assembly;
        Assert.Contains(Prefix + file, assembly.GetManifestResourceNames());

        using var stream = assembly.GetManifestResourceStream(Prefix + file);
        Assert.NotNull(stream);
        Assert.True(stream.Length > 0);

        var magic = Convert.FromHexString(magicHex);
        var head = new byte[magic.Length];
        Assert.Equal(magic.Length, stream.Read(head, 0, head.Length));
        Assert.Equal(magic, head);
    }
}
