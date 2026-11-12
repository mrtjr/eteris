using System.IO;
using Eteris.Browser.Downloads;

namespace Eteris.Browser.Tests;

public sealed class DownloadRiskTests
{
    [Theory]
    [InlineData("Fatura.pdf.exe", true)]
    [InlineData("A.EXE", true)]
    [InlineData("instalar.msi", true)]
    [InlineData("script.ps1", true)]
    [InlineData("abrir.lnk", true)]
    [InlineData("imagem.iso", true)]
    [InlineData("macro.js", true)]
    [InlineData("a.exe. ", true)]
    [InlineData("relatorio.pdf", false)]
    [InlineData("foto.jpg", false)]
    [InlineData("dados.zip", false)]
    [InlineData("semextensao", false)]
    public void ExecutableTypes_AskBeforeOpening(string name, bool expected) => Assert.Equal(expected, DownloadRisk.Executes(name));

    [Fact]
    public void Row_IgnoresClicksInTheFirstSecond()
    {
        var t = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(DownloadRisk.AcceptsClick(t.AddMilliseconds(999), openedByItselfUtc: t, completedUtc: null));
        Assert.False(DownloadRisk.AcceptsClick(t.AddSeconds(5), openedByItselfUtc: t, completedUtc: t.AddSeconds(4.5)));
        Assert.True(DownloadRisk.AcceptsClick(t.AddSeconds(5), openedByItselfUtc: t, completedUtc: t.AddSeconds(4)));
        Assert.True(DownloadRisk.AcceptsClick(t, openedByItselfUtc: null, completedUtc: null));
    }

    [Fact]
    public void Question_ShowsTheWholeNameAndTheSource()
    {
        var text = DownloadRisk.Question("Fatura_novembro_2026_documento_completo.pdf.exe", "exemplo.pt");
        Assert.Contains("«Fatura_novembro_2026_documento_completo.pdf.exe»", text);
        Assert.Contains("exemplo.pt", text);
        Assert.Contains("desconhecida", DownloadRisk.Question("a.exe", null));
    }

    [Fact]
    public void Question_DropsTheCharactersThatReverseReading()
    {
        var text = DownloadRisk.Question("Fatura\u202Efdp.exe", "exemplo.pt");
        Assert.DoesNotContain('\u202E', text);
        Assert.Contains("«Faturafdp.exe»", text);
    }

    [Fact]
    public void Row_IsWiredToTheRules()
    {
        string Source(string file) => File.ReadAllText(Path.Combine(Sync.GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", file));
        var window = Source("MainWindow.Downloads.cs");
        Assert.Contains("DownloadRisk.AcceptsClick(", window);
        Assert.Contains("DownloadRisk.Executes(", window);
        var xaml = Source("MainWindow.xaml");
        Assert.Contains("{Binding NameTail", xaml);
        Assert.DoesNotContain("Text=\"{Binding FileName}\"", xaml);
    }
}
