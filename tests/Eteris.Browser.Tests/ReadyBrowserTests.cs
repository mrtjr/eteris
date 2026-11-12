using System.IO;
using Xunit;

namespace Eteris.Browser.Tests;

// UIX-01: «Recarregar» (e Voltar, Avançar, Parar, Imprimir) numa aba acabada de acordar falavam com um motor ainda por
// inicializar, e a exceção, num `async void`, fechava o Éteris. Só o que está dentro da janela (CEF) não se testa sem
// a janela: o código fica fixado por texto.
public class ReadyBrowserTests
{
    [Fact]
    public void NavigationCommands_UseTheReadyBrowser()
    {
        var reload = Source("MainWindow.Reload.cs");
        Assert.Contains("ReadyBrowser is not { } browser", reload);
        Assert.Contains("catch (Exception)", reload);
        var video = Source("MainWindow.VideoEngine.cs");
        Assert.DoesNotContain("ActiveBrowser?.Back()", video);
        Assert.DoesNotContain("ActiveBrowser?.Stop()", video);
        Assert.DoesNotContain("if (ActiveBrowser is not { } browser) return;", video);
        Assert.DoesNotContain("ActiveBrowser?.Print()", Source("MainWindow.xaml.cs"));
        Assert.DoesNotContain("ActiveBrowser?.Print()", Source("MainWindow.BrowserMenu.cs"));
        Assert.Contains("IsReady(browser)", Source("MainWindow.LocalNetwork.cs"));
        Assert.Contains("IsReady(browser)", Source("MainWindow.Theme.cs"));
        Assert.Contains("!browser.IsBrowserInitialized || browser.GetBrowser() is not { } cef", Source("MainWindow.xaml.cs"));
    }

    private static readonly string Root = FindRoot();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }

    private static string Source(string relative) => File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", relative));
}
