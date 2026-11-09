using System.IO;
using Eteris.Browser.Browser;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

// Funcionalidades de desempenho do motor (Shields/EnginePerformance.cs), cada uma atrás do seu ajuste. E7 do plano de
// desempenho da 0.2.14 (a cache de Voltar/Avançar com 3 páginas por aba, à espera da decisão D-5) e a fase 2: E14 (o que o
// Chrome estável recebe por variações para a CPU e a GPU) e E15 (memória). Todas desligadas até ao seu A/B; os nomes estão
// no EngineFeatureNamesTests.
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

    // A ordem e os nomes do plano (E14: 1 a 6; E15), com os parâmetros dos estudos da configuração de testes da 156.
    [Theory]
    [InlineData(PerfTuning.NoDamageThrottle, "ThrottleRepeatedNoDamageFrames")]
    [InlineData(PerfTuning.BeginFrameRunOrPost, "ExternalBeginFrameSourceWinUsesRunOrPostTask")]
    [InlineData(PerfTuning.SingleVideoThrottle, "SingleVideoFrameRateThrottling")]
    [InlineData(PerfTuning.GpuPersistentCache, "GpuPersistentCache")]
    [InlineData(PerfTuning.GpuPersistentCache, "GpuPersistentCacheMetadata")]
    [InlineData(PerfTuning.DelegatedCompositing, "DelegatedCompositing")]
    [InlineData(PerfTuning.SkiaGraphite, "SkiaGraphite:dawn_skip_validation/true/dawn_backend_validation/false")]
    [InlineData(PerfTuning.HiddenWebGlBuffers, "WebGLDiscardBackBuffer")]
    [InlineData(PerfTuning.HiddenWebGlBuffers, "OffscreenCanvasPropagateVisibility")]
    [InlineData(PerfTuning.KillSpareOnPressure, "KillSpareRenderOnMemoryPressure")]
    [InlineData(PerfTuning.SpareCriticalPressure, "SpareRPHUseCriticalMemoryPressure")]
    [InlineData(PerfTuning.SortActiveSlotSpans, "PartitionAllocSortActiveSlotSpans")]
    [InlineData(PerfTuning.V8SqrtLimits, "V8Flag_sqrt_allocation_limits")]
    public void PhaseTwo_EntriesAreThePlanOnes(string tweak, string feature)
    {
        var entry = Assert.Single(EnginePerformance.Enabled, f => f.Feature == feature);
        Assert.Equal(tweak, entry.Tweak);
        Assert.False(entry.Disable);
    }

    // Cada entrada tem um ajuste do motor, desligado por omissão (nada da fase 2 liga sem o seu A/B).
    [Fact]
    public void EveryEntry_HasAnEngineTweakOffByDefault()
        => Assert.All(EnginePerformance.Enabled, f =>
        {
            var tweak = Assert.Single(PerfTuning.Tweaks, t => t.Id == f.Tweak);
            Assert.True(tweak.Engine, f.Feature);
            Assert.False(tweak.OnByDefault, f.Feature);
            Assert.True(tweak.BeforeFirstPaint, f.Feature);
            Assert.False(string.IsNullOrWhiteSpace(f.What));
        });

    // O que muda a impressão digital (o Graphite muda o canvas) espera pela decisão do dono (D-3) e nunca liga por omissão.
    [Fact]
    public void FingerprintChanges_WaitForTheOwnerAndAreOff()
    {
        var changes = EnginePerformance.Enabled.Where(f => f.ChangesFingerprint).ToList();
        Assert.Equal(["SkiaGraphite"], changes.Select(f => EnginePerformance.FeatureName(f.Feature)));
        Assert.All(changes, f =>
        {
            var tweak = Assert.Single(PerfTuning.Tweaks, t => t.Id == f.Tweak);
            Assert.Equal("D-3", tweak.Waits);
            Assert.DoesNotContain(f.Tweak, PerfTuning.Defaults);
        });
    }

    // As de GPU (uma queda do processo da GPU antes da 1.ª página é uma falha do arranque, D-18) ficam desligadas por
    // omissão; a rede de segurança em dois degraus desliga-as primeiro (PerfTuning.Suspend) e só depois o sandbox.
    [Fact]
    public void GpuEntries_AreOffByDefault()
    {
        var gpu = EnginePerformance.Enabled.Where(f => f.Gpu).Select(f => f.Tweak).ToHashSet();
        Assert.Superset(new HashSet<string> { PerfTuning.SkiaGraphite, PerfTuning.DelegatedCompositing, PerfTuning.GpuPersistentCache }, gpu);
        Assert.All(gpu, id => Assert.DoesNotContain(id, PerfTuning.Defaults));
    }

    // Por omissão a linha de comandos do motor fica como estava: nenhuma das entradas, em nenhuma lista.
    [Fact]
    public void ByDefault_NothingIsAdded()
    {
        var args = new Dictionary<string, string>(CefConfigurator.Build("p").CefCommandLineArgs);
        var enabled = EngineHardening.Switch(args, "enable-features");
        var disabled = EngineHardening.Switch(args, "disable-features");
        Assert.All(EnginePerformance.Enabled, f => Assert.DoesNotContain(f.Feature, f.Disable ? disabled : enabled));
        Assert.Empty(EnginePerformance.ActiveIn(args));
    }

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

    // Um ajuste liga só as suas entradas.
    [Fact]
    public void OneTweak_AddsOnlyItsEntries()
    {
        var args = new Dictionary<string, string>();
        EnginePerformance.Apply(args, id => id == PerfTuning.HiddenWebGlBuffers);
        Assert.Equal(["WebGLDiscardBackBuffer", "OffscreenCanvasPropagateVisibility"], EngineHardening.Switch(args, "enable-features"));
        Assert.False(args.ContainsKey("disable-features"));
    }

    // A cache persistente da GPU escreve uma pasta que o disable-gpu-shader-disk-cache não trava: só com o e3 (D-4).
    [Fact]
    public void GpuPersistentCache_NeedsTheShaderCache()
    {
        var alone = new Dictionary<string, string>();
        EnginePerformance.Apply(alone, id => id == PerfTuning.GpuPersistentCache);
        Assert.False(alone.ContainsKey("enable-features"));

        var withCache = new Dictionary<string, string>();
        EnginePerformance.Apply(withCache, id => id is PerfTuning.GpuPersistentCache or PerfTuning.ShaderCache);
        Assert.Equal(["GpuPersistentCache", "GpuPersistentCacheMetadata"], EngineHardening.Switch(withCache, "enable-features"));
    }

    // O Graphite guarda os pipelines no disco por omissão no Windows (SkiaGraphiteUsePersistentCache): sem o e3, não.
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Graphite_KeepsNothingOnDiskWithoutTheShaderCache(bool shaderCache, bool guard)
    {
        var args = new Dictionary<string, string>();
        EnginePerformance.Apply(args, id => id == PerfTuning.SkiaGraphite || (shaderCache && id == PerfTuning.ShaderCache));
        Assert.Contains("SkiaGraphite:dawn_skip_validation/true/dawn_backend_validation/false", EngineHardening.Switch(args, "enable-features"));
        Assert.Equal(guard, EngineHardening.Switch(args, "disable-features").Contains("SkiaGraphiteUsePersistentCache"));
    }

    // As funcionalidades do V8 passam pelo enable-features com o prefixo V8Flag_ (o gin passa-as ao V8 como --flag).
    [Fact]
    public void V8Flags_GoThroughEnableFeatures()
    {
        var args = new Dictionary<string, string>();
        EnginePerformance.Apply(args, id => id == PerfTuning.V8SqrtLimits);
        Assert.Equal(["V8Flag_sqrt_allocation_limits"], EngineHardening.Switch(args, "enable-features"));
        Assert.All(EnginePerformance.Enabled.Where(f => f.Feature.StartsWith(EnginePerformance.V8FlagPrefix, StringComparison.Ordinal)),
            f => Assert.Matches("^V8Flag_[a-z0-9_]+$", EnginePerformance.FeatureName(f.Feature)));
    }

    // Juntar as do motor a uma lista que já as tem não as repete (o Merge).
    [Fact]
    public void Apply_DoesNotRepeatNames()
    {
        var args = new Dictionary<string, string> { ["enable-features"] = "ThrottleRepeatedNoDamageFrames" };
        EnginePerformance.Apply(args, id => id == PerfTuning.NoDamageThrottle);
        Assert.Equal(["ThrottleRepeatedNoDamageFrames"], EngineHardening.Switch(args, "enable-features"));
    }

    // eteris://privacy/ lê o que está na linha de comandos em vigor.
    [Fact]
    public void ActiveIn_ReadsTheEngineCommandLine()
    {
        var args = new Dictionary<string, string>();
        EnginePerformance.Apply(args, id => id is PerfTuning.NoDamageThrottle or PerfTuning.SkiaGraphite);
        Assert.Equal(["ThrottleRepeatedNoDamageFrames", "SkiaGraphite", "SkiaGraphiteUsePersistentCache"],
            EnginePerformance.ActiveIn(args).Select(f => EnginePerformance.FeatureName(f.Feature)));
    }

    [Fact]
    public void PrivacyPage_NoTuning_IsOptionalAndOff()
    {
        var promise = Promise(new Dictionary<string, string>(CefConfigurator.Build("p").CefCommandLineArgs));
        Assert.Null(promise.Kept);
        Assert.Contains("nenhuma ligada", promise.Detail);
    }

    [Fact]
    public void PrivacyPage_TuningWithoutFingerprintChanges_IsKept()
    {
        var args = new Dictionary<string, string>(CefConfigurator.Build("p").CefCommandLineArgs);
        EnginePerformance.Apply(args, id => id == PerfTuning.NoDamageThrottle);
        var promise = Promise(args);
        Assert.True(promise.Kept);
        Assert.Contains("abranda os fotogramas", promise.Detail);
    }

    // O Graphite liga-se só por interruptor; ligado, a promessa fica ✘ e diz qual e como se desliga.
    [Fact]
    public void PrivacyPage_Graphite_IsFlagged()
    {
        var args = new Dictionary<string, string>(CefConfigurator.Build("p").CefCommandLineArgs);
        EnginePerformance.Apply(args, id => id == PerfTuning.SkiaGraphite);
        var promise = Promise(args);
        Assert.False(promise.Kept);
        Assert.Contains("SkiaGraphite", promise.Detail);
        Assert.Contains(PerfTuning.EngineOffArgument, promise.Detail);
        Assert.DoesNotContain("sem a cache no disco", promise.Detail);
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

    private static PrivacyPage.Promise Promise(Dictionary<string, string> args)
    {
        var state = new PrivacyPage.State(args, PrivacySignals.Headers, GpcScript: true, ThirdPartyCookiesBlocked: true, WebRtcProtected: true,
            SecureDns: null, Language: "pt-PT", HideFromCapture: false, VisibleWindows: 1, HiddenWindows: 0);
        return Assert.Single(PrivacyPage.Promises(state), p => p.Id == "engine-tuning");
    }

    private static readonly string Root = FindRoot();

    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Raiz do repositório não encontrada");
    }
}
