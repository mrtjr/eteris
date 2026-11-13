using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Tests.Sync;

namespace Eteris.Browser.Tests;

// INF-04: dois arranques seguidos que não chegam à 1.ª página nem a um fecho normal, numa instalação, fazem o terceiro
// oferecer procurar e instalar uma versão corrigida antes de arrancar o motor. A rede de segurança do sandbox não cobre
// isto: sem sandbox possível nem existe.
public sealed class StartupRescueTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("eteris-rescue-").FullName;
    private string File_ => Path.Combine(_dir, StartupRescue.FileName);

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Begin_CountsConsecutiveStartsThatNeverBecameHealthy()
    {
        Assert.Equal(0, StartupRescue.Begin(File_));
        Assert.Equal(1, StartupRescue.Begin(File_));
        Assert.Equal(2, StartupRescue.Begin(File_));
    }

    [Fact]
    public void Clear_ResetsTheCount()
    {
        StartupRescue.Begin(File_);
        StartupRescue.Begin(File_);
        StartupRescue.Clear(File_);
        Assert.False(File.Exists(File_));
        Assert.Equal(0, StartupRescue.Begin(File_));
    }

    // Um ficheiro ilegível, cortado ou impossível de escrever nunca derruba o arranque nem conta como arranque falhado.
    [Fact]
    public void BrokenOrMissingFile_CountsZero_AndNeverThrows()
    {
        File.WriteAllText(File_, "{");
        Assert.Equal(0, StartupRescue.Begin(File_));
        foreach (var junk in new[] { "", "{\"Pending\":-4}", "{\"Pending\":\"x\"}", "[1]", "{\"Pending\":99999999999999999999}" })
        {
            File.WriteAllText(File_, junk);
            Assert.Equal(0, StartupRescue.Begin(File_));
        }
        var under = Path.Combine(File_, "x.json");            // «File_» é um ficheiro: a pasta não se pode criar
        Assert.Equal(0, StartupRescue.Begin(under));
        StartupRescue.Clear(under);
    }

    [Fact]
    public void ReadOnlyFile_CountsZero_AndNeverThrows()
    {
        File.WriteAllText(File_, "{\"Pending\":5}");
        File.SetAttributes(File_, FileAttributes.ReadOnly);
        try { Assert.Equal(5, StartupRescue.Begin(File_)); }   // lê-se na mesma; a escrita falha em silêncio
        finally { File.SetAttributes(File_, FileAttributes.Normal); }
        Assert.False(File.Exists(File_ + ".tmp"));
    }

    [Theory]
    [InlineData(1, true, false)] [InlineData(2, true, true)] [InlineData(5, true, true)] [InlineData(2, false, false)]
    public void ShouldOffer_OnlyInstalledAndAfterTwo(int failed, bool installed, bool expected)
        => Assert.Equal(expected, StartupRescue.ShouldOffer(failed, installed));

    [Fact]
    public void Start_OutsideAnInstallation_DoesNothingAndWritesNothing()
    {
        StartupRescue.Start(_dir, [], installed: false);
        Assert.False(File.Exists(File_));
        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public void Program_OffersRescueAfterTheInstanceAndBeforeTheEngine()
    {
        var code = GoogleSyncTests.Src("Eteris.Browser", "Program.cs");
        var rescue = code.IndexOf("StartupRescue.Start(AppPaths.Root, args, TestSwitches.IsInstalled);", StringComparison.Ordinal);
        Assert.True(rescue > code.IndexOf("UpdateService.ApplyPendingAtStartup(args, restartedByUpdater);", StringComparison.Ordinal));
        Assert.True(rescue < code.IndexOf("CefConfigurator.StartFromMain(App.OnCefReady);", StringComparison.Ordinal));
        Assert.True(rescue < code.IndexOf("new App()", StringComparison.Ordinal));
    }

    [Fact]
    public void HealthySites_AlsoClearTheRescueMarker()
    {
        Assert.Equal(2, Regex.Matches(GoogleSyncTests.Src("Eteris.Browser", "MainWindow.PageVisibility.cs"), @"StartupRescue\.Healthy\(\);").Count);
        Assert.Contains("StartupRescue.Healthy(now: true);", GoogleSyncTests.Src("Eteris.Browser", "App.xaml.cs"));
    }

    // R-2: nada de novo no disco nem em memória antes da 1.ª página num arranque normal que não seja o da oferta: o ficheiro
    // lê-se e grava-se à mão, sem o JsonSerializer (o seu arranque em frio custa dezenas de milissegundos).
    [Fact]
    public void StartupRescue_DoesNotUseTheJsonSerializer()
    {
        var code = GoogleSyncTests.Src("Eteris.Browser", "Infrastructure", "StartupRescue.cs");
        Assert.DoesNotContain("JsonSerializer.", code);
        Assert.DoesNotContain("System.Text.Json", code);
    }
}
