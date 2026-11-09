using System.IO;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

// Funcionalidades de desempenho do motor (Shields/EnginePerformance.cs), cada uma atrás do seu ajuste. E7 do plano de
// desempenho da 0.2.14: a cache de Voltar/Avançar com 3 páginas por aba (ajuste e7, à espera da decisão D-5).
public class EnginePerformanceTests
{
    [Fact]
    public void Entries_SplitIntoNameAndParameters()
    {
        Assert.Equal("BackForwardCacheSize", EnginePerformance.FeatureName("BackForwardCacheSize:cache_size/3"));
        Assert.Equal("Outra", EnginePerformance.FeatureName("Outra"));
        Assert.Equal(new Dictionary<string, string> { ["cache_size"] = "3" }, EnginePerformance.Parameters("BackForwardCacheSize:cache_size/3"));
        Assert.Empty(EnginePerformance.Parameters("Outra"));
    }

    [Fact]
    public void BackForwardCache_KeepsThreePagesPerTab()
    {
        var entry = Assert.Single(EnginePerformance.Enabled, f => f.Tweak == PerfTuning.SmallBackForwardCache);
        Assert.Equal("BackForwardCacheSize", EnginePerformance.FeatureName(entry.Feature));
        Assert.Equal("3", EnginePerformance.Parameters(entry.Feature)["cache_size"]);
    }

    [Fact]
    public void Tweak_IsOffByDefaultAndWaitsForTheOwner()
    {
        var tweak = Assert.Single(PerfTuning.Tweaks, t => t.Id == PerfTuning.SmallBackForwardCache);
        Assert.False(tweak.OnByDefault);
        Assert.Equal("D-5", tweak.Waits);
    }

    // Por omissão a linha de comandos do motor fica como estava.
    [Fact]
    public void ByDefault_NothingIsAdded()
        => Assert.DoesNotContain(EngineHardening.Switch(new Dictionary<string, string>(CefConfigurator.Build("p").CefCommandLineArgs), "enable-features"),
            f => f.StartsWith("BackForwardCacheSize", StringComparison.Ordinal));

    // Com o ajuste, junta-se ao enable-features (nunca o substitui: as promessas de privacidade ficam).
    [Fact]
    public void WithTheTweak_ItIsMergedIntoEnableFeatures()
    {
        var args = new Dictionary<string, string> { ["enable-features"] = "ThirdPartyStoragePartitioning" };
        EnginePerformance.Apply(args, id => id == PerfTuning.SmallBackForwardCache);
        var enabled = EngineHardening.Switch(args, "enable-features");
        Assert.Contains("ThirdPartyStoragePartitioning", enabled);
        Assert.Contains("BackForwardCacheSize:cache_size/3", enabled);
    }

    [Fact]
    public void WithoutAnyTweak_TheArgumentsAreUntouched()
    {
        var args = new Dictionary<string, string>();
        EnginePerformance.Apply(args, _ => false);
        Assert.Empty(args);
    }

    // EngineHardening.Apply passa pela lista antes de guardar o que o motor recebeu (eteris://privacy/ lê o Applied).
    [Fact]
    public void Hardening_AppliesThePerformanceListBeforeTheSnapshot()
    {
        var code = File.ReadAllText(Path.Combine(Root, "src", "Eteris.Browser", "Shields", "EngineHardening.cs"));
        var apply = code.IndexOf("EnginePerformance.Apply(args, Infrastructure.PerfTuning.IsOn);", StringComparison.Ordinal);
        var snapshot = code.IndexOf("Applied = new Dictionary<string, string>(args);", StringComparison.Ordinal);
        Assert.True(apply > 0 && snapshot > apply);
    }

    private static readonly string Root = FindRoot();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }
}
