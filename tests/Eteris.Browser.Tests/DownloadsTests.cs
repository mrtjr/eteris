using System.IO;
using Eteris.Browser.Downloads;

namespace Eteris.Browser.Tests;

public sealed class DownloadsTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "eteris-dl-" + Guid.NewGuid())).FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Name_SplitsSoTheExtensionIsNeverTrimmed()
    {
        Assert.Equal(("Fatura_novembro_2026_documento_comple", "to.pdf.exe"), DownloadFormat.SplitName("Fatura_novembro_2026_documento_completo.pdf.exe"));
        Assert.Equal(("", "curto.pdf"), DownloadFormat.SplitName("curto.pdf"));
        Assert.Equal(("sem", "extensao"), DownloadFormat.SplitName("semextensao"));
        var wide = DownloadFormat.SplitName(new string('W', 80) + ".zip");
        Assert.Equal("WWWWWW.zip", wide.Tail);
        Assert.Equal(74, wide.Head.Length);
        // U+202E inverte a leitura: um ficheiro «Fatura» + U+202E + «fdp.exe» lia-se «Faturaexe.pdf». Os caracteres de formato e de controlo saem.
        Assert.Equal(("Fat", "urafdp.exe"), DownloadFormat.SplitName("Fatura\u202Efdp.exe"));
    }

    [Fact]
    public void UniquePath_FreeName_IsKept()
        => Assert.Equal(Path.Combine(_dir, "relatório.pdf"), DownloadPaths.Unique(_dir, "relatório.pdf"));

    [Fact]
    public void UniquePath_TakenName_GetsNumberBeforeExtension()
    {
        File.WriteAllText(Path.Combine(_dir, "relatório.pdf"), "");
        File.WriteAllText(Path.Combine(_dir, "relatório (1).pdf"), "");
        Assert.Equal(Path.Combine(_dir, "relatório (2).pdf"), DownloadPaths.Unique(_dir, "relatório.pdf"));
    }

    [Theory]
    [InlineData("", "transferência")]
    [InlineData("..\\..\\Windows\\evil.exe", "evil.exe")]
    [InlineData("a<b>c:d.txt", "a_b_c_d.txt")]
    public void UniquePath_SanitisesSuggestedName(string suggested, string expected)
        => Assert.Equal(Path.Combine(_dir, expected), DownloadPaths.Unique(_dir, suggested));

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(999, "999 B")]
    [InlineData(1536, "1,5 KB")]
    [InlineData(3_355_443, "3,2 MB")]
    [InlineData(5_368_709_120, "5,0 GB")]
    public void Size_IsHumanReadablePortuguese(long bytes, string expected)
        => Assert.Equal(expected, DownloadFormat.Size(bytes));

    [Fact]
    public void Progress_KnownTotal_ShowsBothSizesAndSpeed()
        => Assert.Equal("3,2 MB de 10,0 MB · 1,0 MB/s", DownloadFormat.Progress(3_355_443, 10_485_760, 1_048_576));

    [Fact]
    public void Progress_UnknownTotal_ShowsReceivedOnly()
        => Assert.Equal("3,2 MB · 1,0 MB/s", DownloadFormat.Progress(3_355_443, 0, 1_048_576));

    [Fact]
    public void ViewModel_Update_TracksProgressAndCompletion()
    {
        var vm = new DownloadsViewModel();
        vm.Started(7, "C:\\x\\a.zip", "a.zip");
        vm.Update(7, received: 50, total: 200, speed: 10, complete: false, cancelled: false);
        Assert.Equal(25, vm.Items[0].Percent);
        Assert.True(vm.HasActive);
        vm.Update(7, received: 200, total: 200, speed: 0, complete: true, cancelled: false);
        Assert.True(vm.Items[0].IsComplete);
        Assert.False(vm.HasActive);
    }

    [Fact]
    public void ViewModel_NewestFirst_AndOverallProgressAveragesActive()
    {
        var vm = new DownloadsViewModel();
        vm.Started(1, "C:\\x\\a", "a");
        vm.Started(2, "C:\\x\\b", "b");
        vm.Update(1, 50, 100, 0, false, false);
        vm.Update(2, 100, 100, 0, false, false);
        Assert.Equal("b", vm.Items[0].FileName);
        Assert.Equal(75, vm.OverallPercent);
    }

    [Fact]
    public void ViewModel_UpdateForUnknownId_IsIgnored()
    {
        var vm = new DownloadsViewModel();
        vm.Update(99, 1, 2, 0, false, false);
        Assert.Empty(vm.Items);
    }
}
