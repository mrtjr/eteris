using System.IO;
using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

// INT-07, VAU-09 (auditoria 3): as rotas eteris:// só respondem a páginas internas. A fábrica do esquema pergunta, no
// processo do navegador, quem pede (o documento de topo do separador); as molduras e os sub-recursos de uma página web
// são recusados (403), e o cofre só fala com a própria página. Redirecionamentos e molduras de páginas web para
// eteris:// são cancelados em OnBeforeBrowse.
public sealed class InternalAccessTests
{
    [Theory]
    [InlineData("https://a.example/", "eteris://settings/", true, true)]            // abrir a página: sempre
    [InlineData(null, "eteris://newtab/", true, true)]
    [InlineData("https://a.example/", "eteris://settings/", false, false)]          // moldura numa página web
    [InlineData("https://a.example/", "eteris://history/", false, false)]
    [InlineData("https://a.example/", "eteris://settings/set?k=cookies&v=false&t=x", false, false)]
    [InlineData("eteris://settings/", "eteris://settings/set?k=cookies&v=false&t=x", false, true)]
    [InlineData("eteris://newtab/", "eteris://newtab/wallpaper/coast.jpg", false, true)]
    [InlineData("eteris://error/?code=-105", "eteris://error/continue?url=x&s=y", true, true)]
    [InlineData("https://a.example/", "eteris://passwords/list?t=x", true, false)]  // cofre: nunca fora da própria página
    [InlineData("eteris://settings/", "eteris://passwords/list?t=x", false, false)]
    [InlineData("eteris://passwords/", "eteris://passwords/list?t=x", false, true)]
    [InlineData("eteris://autofill/", "eteris://autofill/reveal?t=x", false, true)]
    [InlineData(null, "eteris://settings/set?k=x&v=y&t=z", false, false)]
    [InlineData("eteris://settings/", "https://a.example/", false, false)]
    public void Serves_OnlyInternalPagesReachTheRoutes(string? top, string request, bool mainFrame, bool expected)
        => Assert.Equal(expected, InternalPages.Serves(top, request, mainFrame));

    [Theory]
    [InlineData("https://a.example/", "eteris://settings/", true, true, true)]      // 302 para eteris://
    [InlineData("https://a.example/", "eteris://settings/", false, false, true)]    // moldura de uma página web
    [InlineData("eteris://settings/", "eteris://settings/status", false, false, false)]
    [InlineData("https://a.example/", "eteris://settings/", true, false, false)]    // navegação normal (barra, favorito)
    [InlineData("https://a.example/", "https://b.example/", true, true, false)]
    public void Navigation_RedirectsAndForeignFrames_AreCancelled(string? top, string target, bool mainFrame, bool redirect, bool expected)
        => Assert.Equal(expected, InternalPages.BlocksNavigation(top, target, mainFrame, redirect));

    [Fact]
    public void TopOf_FallsBackToTheNavigationInFlight()
    {
        InternalPages.NoteNavigation(990_001, "eteris://newtab/");
        Assert.Equal("eteris://newtab/", InternalPages.TopOf(990_001, ""));
        Assert.Equal("https://a.example/", InternalPages.TopOf(990_001, "https://a.example/"));
        Assert.Null(InternalPages.TopOf(990_002, null));
    }

    [Fact]
    public void Factory_AsksWhoIsAsking()
    {
        string Source(params string[] parts) => File.ReadAllText(Path.Combine([Sync.GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", .. parts]));
        var factory = Source("Browser", "EterisSchemeHandlerFactory.cs");
        Assert.Contains("InternalPages.Serves(", factory);
        Assert.Contains("HttpStatusCode.Forbidden", factory);
        Assert.Contains("\"X-Frame-Options\"] = \"SAMEORIGIN\"", factory);
        Assert.Contains("\"X-Content-Type-Options\"] = \"nosniff\"", factory);
        // As respostas de ResourceHandler.FromString (quase todas as páginas) não aceitam cabeçalhos: a fábrica converte-as.
        Assert.Contains("is ByteArrayResourceHandler bytes", factory);
        var handler = Source("Shields", "PrivacyRequestHandler.cs");
        Assert.Contains("InternalPages.BlocksNavigation(", handler);
        Assert.Equal(2, handler.Split("InternalPages.NoteNavigation(").Length - 1);
    }

    // O facto em que a conversão da fábrica assenta (CefSharp 156): FromString não devolve um ResourceHandler, por isso
    // os Headers não chegavam às Definições, ao Histórico nem à nova aba (visto por CDP: X-Frame-Options em falta).
    [Fact]
    public void FromString_IsNotAResourceHandler()
        => Assert.IsType<CefSharp.ByteArrayResourceHandler>(CefSharp.ResourceHandler.FromString("x", System.Text.Encoding.UTF8, false, "text/plain"));
}
