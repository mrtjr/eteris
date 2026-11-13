using System.IO;
using CefSharp;
using Eteris.Browser.Browser;

namespace Eteris.Browser.Tests;

// Achado 4: o recorte ao vivo recarrega sozinho (a cada 5, 15 ou 60 min) e cada carga pedia o foco ao Windows
// (OnSetFocus com FocusSourceNavigation): a janela do recorte, sempre por cima, tirava o teclado à aba ou à barra de
// endereço onde o utilizador estava a escrever. O recorte recusa o foco que vem de uma carga; o que vem de um clique do
// utilizador (o Windows dá-o à janela nativa) continua a valer, porque o recorte é interativo.
public class ClipFocusTests
{
    [Fact]
    public void ClipFocus_RefusesOnlyTheFocusALoadAsksFor()
    {
        Assert.True(ClipFocusHandler.Cancels(CefFocusSource.FocusSourceNavigation));
        Assert.False(ClipFocusHandler.Cancels(CefFocusSource.FocusSourceSystem));
    }

    [Fact]
    public void ClipWindow_UsesTheClipFocusHandler()
    {
        var code = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "ClipWindow.xaml.cs"));
        var browser = code[code.IndexOf("_browser = new ChromiumWebBrowser(url)", StringComparison.Ordinal)..];
        browser = browser[..browser.IndexOf("};", StringComparison.Ordinal)];
        Assert.Contains("FocusHandler = new ClipFocusHandler(),", browser);
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
