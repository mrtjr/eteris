using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Eteris.Browser.Data;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Tabs;

namespace Eteris.Browser.Tests;

// E13 do plano de desempenho da 0.2.14 (ajuste e13, desligado por omissão até à decisão D-6): com pouca memória, as abas
// adormecem uma de cada vez, a usada há mais tempo primeiro, com as isenções; e a rolagem volta ao acordar.
public class MemoryPressureTests
{
    private static readonly DateTime Now = new(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
    private const ulong Gb = 1UL << 30;

    [Fact]
    public void Tweak_IsOffByDefault_WaitingForD6()
    {
        var tweak = Assert.Single(PerfTuning.Tweaks, t => t.Id == PerfTuning.SleepUnderPressure);
        Assert.False(tweak.OnByDefault);
        Assert.False(tweak.BeforeFirstPaint);
        Assert.Equal("D-6", tweak.Waits);
    }

    // ---------- O sinal ----------

    [Fact]
    public void Low_WhenWindowsSaysSo()
        => Assert.True(MemoryPressure.IsLow(lowMemorySignal: true, availableCommit: 20 * Gb, totalCommit: 40 * Gb));

    [Fact]
    public void Low_WhenCommitIsAlmostGone()
    {
        // Como o Firefox: o commit livre conta, mesmo com RAM livre (as falhas por falta de commit no Windows).
        Assert.True(MemoryPressure.IsLow(false, MemoryPressure.LowCommitBytes - 1, 40 * Gb));
        Assert.True(MemoryPressure.IsLow(false, 1 * Gb, 40 * Gb)); // < 5% de 40 GB
    }

    [Fact]
    public void NotLow_WithRoomToSpare()
    {
        Assert.False(MemoryPressure.IsLow(false, 10 * Gb, 40 * Gb));
        // Sem leitura (a chamada falhou): não se inventa pressão.
        Assert.False(MemoryPressure.IsLow(false, 0, 0));
    }

    [Theory]
    [InlineData("1", false, true)]
    [InlineData(" 1 ", false, true)]
    [InlineData("0", false, false)]
    [InlineData("sim", false, false)]
    [InlineData(null, false, false)]
    [InlineData("1", true, false)] // inerte numa instalação, como os outros ETERIS_TEST_*
    public void SimulatedSignal_OnlyOutsideAnInstall(string? value, bool installed, bool expected)
        => Assert.Equal(expected, PerfDiagnostics.ParsePressure(value, installed));

    // ---------- Que aba adormece ----------

    private static MemoryPressure.Candidate<string> Tab(string name, int minutesAgo, bool onScreen = false, bool loading = false,
        bool audible = false, bool locked = false, bool pinned = false, bool capturing = false, bool playing = false)
        => new(name, onScreen, loading, audible, locked, pinned, capturing, playing, Now.AddMinutes(-minutesAgo));

    [Fact]
    public void Picks_TheLeastRecentlyUsed()
        => Assert.Equal("antiga", MemoryPressure.Pick([Tab("recente", 2), Tab("antiga", 50), Tab("meia", 10)]));

    [Fact]
    public void Exemptions_AreNeverPicked()
    {
        // À vista, a carregar, com som, trancada, fixada, com câmara/microfone/ecrã, ou com multimédia a tocar sem som
        // (um vídeo em Picture-in-Picture): fica acordada mesmo sendo a mais antiga.
        var exempt = new[]
        {
            Tab("vista", 90, onScreen: true), Tab("carrega", 90, loading: true), Tab("som", 90, audible: true),
            Tab("trancada", 90, locked: true), Tab("fixada", 90, pinned: true), Tab("chamada", 90, capturing: true),
            Tab("pip", 90, playing: true),
        };
        Assert.Null(MemoryPressure.Pick(exempt));
        Assert.Equal("livre", MemoryPressure.Pick([.. exempt, Tab("livre", 1)]));
    }

    [Fact]
    public void NothingToPick_WhenNoTabs() => Assert.Null(MemoryPressure.Pick(Array.Empty<MemoryPressure.Candidate<string>>()));

    // Uma aba por intervalo, como o Firefox: um falso positivo da pressão custa no máximo uma aba.
    [Fact]
    public void OneTabPerInterval()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), MemoryPressure.Interval);
        Assert.DoesNotMatch(@"foreach[^\n]*Pick\(", Source("MainWindow.Pressure.cs"));
    }

    // ---------- Modos (as definições) ----------

    [Theory]
    [InlineData(60, true, true)]   // moderado: pressão + 60 min
    [InlineData(20, true, true)]   // equilibrado: pressão + 20 min (o de hoje, sem pressão faz o mesmo)
    [InlineData(5, true, true)]    // máximo: pressão + 5 min
    [InlineData(20, false, false)] // só tempo (o recuo do plano)
    [InlineData(0, true, false)]   // «Nunca»: nem com pressão
    public void Modes(int sleepMinutes, bool onLowMemory, bool expected)
        => Assert.Equal(expected, MemoryPressure.Applies(new UserSettings { SleepMinutes = sleepMinutes, SleepOnLowMemory = onLowMemory }));

    [Theory]
    [InlineData(60, "moderado")]
    [InlineData(20, "equilibrado")]
    [InlineData(5, "máximo")]
    [InlineData(0, "nunca")]
    [InlineData(45, "personalizado")]
    public void ModeNames(int minutes, string expected) => Assert.Equal(expected, MemoryPressure.ModeName(minutes));

    [Fact]
    public void Setting_DefaultsOn_AndOldFilesReadOn()
    {
        Assert.True(new UserSettings().SleepOnLowMemory);
        Assert.True(JsonSerializer.Deserialize<UserSettings>("{\"SleepMinutes\":20}")!.SleepOnLowMemory);
        Assert.True(JsonSerializer.Deserialize("{\"SleepMinutes\":20}", StartupJsonContext.Default.UserSettings)!.SleepOnLowMemory);
        Assert.False(new UserSettings().With("sleepPressure", "false")!.SleepOnLowMemory);
        Assert.Null(new UserSettings().With("sleepPressure", "talvez"));
    }

    // A opção só aparece nas Definições com o ajuste ligado (sem ele não faria nada).
    [Fact]
    public void SettingsPage_ShowsTheOptionOnlyWithTheTweak()
    {
        Assert.Contains("sleepPressure = PerfTuning.IsOn(PerfTuning.SleepUnderPressure) ? settings.SleepOnLowMemory : (bool?)null",
            Source(Path.Combine("Browser", "SettingsPage.cs")));
        var html = Source(Path.Combine("Browser", "settings.html"));
        Assert.Contains("id=\"sleepPressureItem\" hidden", html);
        Assert.Contains("data-key=\"sleepPressure\"", html);
        Assert.Contains(".item[hidden] { display: none; }", html);
    }

    // ---------- Rolagem ----------

    [Fact]
    public void Scroll_IsKeptOnlyWhenThereIsOne()
    {
        Assert.Equal(new SleepScroll("https://a.pt/x", 1234.5), SleepScroll.From("https://a.pt/x", 1234.5));
        Assert.Null(SleepScroll.From("https://a.pt/x", 0.0));
        Assert.Null(SleepScroll.From("https://a.pt/x", null));
        Assert.Null(SleepScroll.From("https://a.pt/x", "texto"));
        Assert.Null(SleepScroll.From("https://a.pt/x", double.NaN));
    }

    [Fact]
    public void Scroll_RestoresOnlyOnTheSamePage()
    {
        var scroll = new SleepScroll("https://a.pt/x#topo", 800);
        Assert.True(scroll.Matches("https://a.pt/x#topo"));
        Assert.False(scroll.Matches("https://a.pt/y"));
    }

    // O número vai para o script sempre com ponto (a cultura pt-PT escreveria vírgula) e nunca por cima de uma rolagem
    // que a pessoa já fez.
    [Fact]
    public void Scroll_RestoreScript()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("pt-PT");
            var script = new SleepScroll("https://a.pt/", 1234.5).RestoreScript;
            Assert.Contains("1234.5", script);
            Assert.Contains("window.scrollY===0", script);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    // Todos os caminhos do sono (por tempo, por pressão, «Adormecer as abas de fundo») passam pelo PutToSleep, que só com o
    // ajuste guarda a rolagem antes; sem ele, o mesmo de sempre (largar o motor já).
    [Fact]
    public void AllSleepPaths_GoThroughPutToSleep()
    {
        var sleep = Source("MainWindow.Sleep.cs");
        Assert.Single(Regex.Matches(sleep, @"tab\.IsSleeping = true;"));
        Assert.Contains("if (!PerfTuning.IsOn(PerfTuning.SleepUnderPressure) || !_browsers.TryGetValue(tab, out var browser)", sleep);
        Assert.Contains("RestoreSleptScroll(tab, browser, url);", Source("MainWindow.xaml.cs"));
        Assert.Contains("PutToSleep(tab);", Source("MainWindow.Selection.cs"));
        Assert.DoesNotContain("tab.IsSleeping = true;", Source("MainWindow.Selection.cs"));
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
