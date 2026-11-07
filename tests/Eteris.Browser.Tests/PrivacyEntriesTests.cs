using System.IO;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Palette;

namespace Eteris.Browser.Tests;

// B9 + B10 à mão: o Observatório e eteris://privacy/ (relatório e auto-teste) chegam-se pelo teclado, pelo menu do
// Éteris, pela paleta, pelas Definições e pela linha de comandos, e estão nas Novidades.
public class PrivacyEntriesTests
{
    [Fact]
    public void Observatory_HasAShortcut_InTheSheet_AndBeforeViewSource()
    {
        Assert.Contains(ShortcutSheet.Groups.SelectMany(g => g.Items), s => s.Keys == "Ctrl+Shift+U" && s.Action.StartsWith("Observatório"));
        var main = Read("src", "Eteris.Browser", "MainWindow.xaml.cs");
        var observatory = main.IndexOf("if (ctrl && shift && key == Key.U) return ToggleObservatory;", StringComparison.Ordinal);
        Assert.True(observatory > 0);
        Assert.True(observatory < main.IndexOf("if (ctrl && key == Key.U) return ViewSource;", StringComparison.Ordinal));
    }

    [Fact]
    public void WhatsNew_LeadsWithTheObservatoryAndTheReport()
    {
        Assert.Equal("Observatório do site", WhatsNewPage.Highlights[0].Title);
        Assert.Equal("Ctrl+Shift+U", WhatsNewPage.Highlights[0].Keys);
        Assert.Equal("Relatório e auto-teste de privacidade", WhatsNewPage.Highlights[1].Title);
    }

    [Fact]
    public void SelfTest_OpensFromTheCommandLine_ButNoOtherSubpage()
    {
        Assert.Equal(SelfTest.Url, StartupArgs.InitialUrl([SelfTest.Url]));
        Assert.Equal(PrivacyPage.Url, StartupArgs.InitialUrl([PrivacyPage.Url]));
        Assert.Null(StartupArgs.InitialUrl(["eteris://privacy/clear-report"]));
        Assert.Null(StartupArgs.InitialUrl(["eteris://privacy/teste?x=1"]));
    }

    [Fact]
    public void Settings_LinkToTheReportAndTheSelfTest()
    {
        var html = SettingsPage.Render(new UserSettings());
        Assert.Contains("href=\"eteris://privacy/\"", html);
        Assert.Contains("href=\"eteris://privacy/teste\"", html);
        Assert.Contains("Relatório e promessas de privacidade", html);
    }

    [Fact]
    public void MenuAndPalette_OfferTheReportAndTheSelfTest()
    {
        Assert.Contains("Row(\"\\uE72E\", \"Privacidade: relatório e auto-teste\", () => OpenInternal(PrivacyPage.Url));",
            Read("src", "Eteris.Browser", "MainWindow.BrowserMenu.cs"));
        var palette = Read("src", "Eteris.Browser", "MainWindow.Palette.cs");
        Assert.Contains("\"Relatório de privacidade\"", palette);
        Assert.Contains("() => OpenInternal(SelfTest.Url)", palette);
        Assert.Contains("\"Observatório do site\", \"Quem está a observar esta página, ao vivo, por empresa\", \"Ctrl+Shift+U\"", palette);
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root(), .. parts]));

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
