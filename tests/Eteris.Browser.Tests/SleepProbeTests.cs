using System.IO;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// Auditoria 4 (DAD-03, DES-01, MOT-15): antes de adormecer por tempo ou por pressão, o Éteris pergunta à página se tem
// texto por enviar ou Picture-in-Picture; sem resposta, adormece como antes.
public sealed class SleepProbeTests
{
    [Fact]
    public void Parse_ReadsTheAnswer()
        => Assert.Equal(new SleepProbe(120.5, true, false, true), SleepProbe.Parse("{\"y\":120.5,\"d\":true,\"p\":false,\"f\":true}"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{")]
    [InlineData(42.0)]
    public void Parse_Garbage_IsNull(object? result) => Assert.Null(SleepProbe.Parse(result));

    [Fact]
    public void KeepsAwake_WithUnsentTextOrPictureInPicture()
    {
        Assert.True(new SleepProbe(0, true, false, false).KeepsAwake);
        Assert.True(new SleepProbe(0, false, true, false).KeepsAwake);
        Assert.False(new SleepProbe(0, false, false, false).KeepsAwake);
        Assert.Equal(SleepProbe.PlayerFrameKeepsAwake, new SleepProbe(0, false, false, true).KeepsAwake);   // decisão D1
        Assert.False(SleepProbe.PlayerFrameKeepsAwake);
        Assert.Equal(TimeSpan.FromMinutes(5), SleepProbe.HoldFor);
    }

    [Fact]
    public void TimeAndPressureSleep_AskThePageFirst()
    {
        var sleep = Source("MainWindow.Sleep.cs");
        Assert.Contains("PageScript.EvaluateAsync(browser, SleepProbe.Script", sleep);
        Assert.Contains("isCapturing: IsCapturingTab(tab)", sleep);
        Assert.Contains("SleepPolicy.WasSuspended(_lastSleepRoundUtc, now)", sleep);
        Assert.Contains("MemoryPressure.Pick(candidates, DateTime.UtcNow)", Source("MainWindow.Pressure.cs"));
        Assert.DoesNotContain("Nunca adormecem a tocar áudio", Source("Browser/settings.html"));
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
