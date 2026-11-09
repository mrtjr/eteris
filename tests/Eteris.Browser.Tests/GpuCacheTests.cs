using System.IO;
using System.Text.RegularExpressions;
using Eteris.Browser.Browser;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

// E3 do plano de desempenho da 0.2.14 (ajuste e3, à espera da decisão D-4): a cache dos shaders da GPU no disco. Sem o
// ajuste, tudo como desde a 1.ª versão (disable-gpu-shader-disk-cache). Com ele, as caches apagam-se com os dados dos
// sites e depois de uma janela privada, porque as globais não são por janela.
public class GpuCacheTests
{
    [Fact]
    public void ByDefault_TheEngineKeepsNoShaderCache()
        => Assert.Equal("1", CefConfigurator.Build("p").CefCommandLineArgs["disable-gpu-shader-disk-cache"]);

    [Fact]
    public void WithTheTweak_TheEngineKeepsTheShaderCache()
        => Assert.False(CefConfigurator.Build("p", shaderCache: true).CefCommandLineArgs.ContainsKey("disable-gpu-shader-disk-cache"));

    [Fact]
    public void Tweak_IsOffByDefaultAndWaitsForTheOwner()
    {
        var tweak = Assert.Single(PerfTuning.Tweaks, t => t.Id == PerfTuning.ShaderCache);
        Assert.False(tweak.OnByDefault);
        Assert.True(tweak.BeforeFirstPaint);
        Assert.Equal("D-4", tweak.Waits);
    }

    // As pastas verificadas no Chromium 156: globais (chrome_content_browser_client.cc) e por partição
    // (gpu_disk_cache_type.cc). Uma que falte ficava no disco depois de «Apagar ao fechar».
    [Fact]
    public void Folders_AreEveryGpuCacheOfTheEngine()
    {
        Assert.Equal(["ShaderCache", "GrShaderCache", "GraphiteDawnCache"], ProfileCleanup.GlobalGpuCaches);
        Assert.Equal(["GPUCache", "DawnWebGPUCache", "DawnGraphiteCache"], ProfileCleanup.PartitionGpuCaches);
    }

    [Fact]
    public void Remove_DeletesTheGpuCachesAndNothingElse()
    {
        var root = Path.Combine(Path.GetTempPath(), "eteris-gpucache-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var f in ProfileCleanup.GlobalGpuCaches) Directory.CreateDirectory(Path.Combine(root, f, "x"));
            foreach (var f in ProfileCleanup.PartitionGpuCaches) Directory.CreateDirectory(Path.Combine(root, "Default", f, "x"));
            Directory.CreateDirectory(Path.Combine(root, "Default", "Cache"));
            Directory.CreateDirectory(Path.Combine(root, "Default", "Local Storage"));
            File.WriteAllText(Path.Combine(root, "Local State"), "{}");

            Assert.Equal(6, ProfileCleanup.RemoveGpuCaches(root));
            Assert.All(ProfileCleanup.GlobalGpuCaches, f => Assert.False(Directory.Exists(Path.Combine(root, f))));
            Assert.All(ProfileCleanup.PartitionGpuCaches, f => Assert.False(Directory.Exists(Path.Combine(root, "Default", f))));
            Assert.True(Directory.Exists(Path.Combine(root, "Default", "Cache")));
            Assert.True(Directory.Exists(Path.Combine(root, "Default", "Local Storage")));
            Assert.True(File.Exists(Path.Combine(root, "Local State")));
            Assert.Equal(0, ProfileCleanup.RemoveGpuCaches(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ClearOnExit_RemovesThem()
        => Assert.True(ProfileCleanup.ShouldRemoveGpuCaches(clearOnExit: true));

    // Ao sair, com o motor já fechado (o processo da GPU tem as pastas abertas).
    [Fact]
    public void Exit_RemovesThemAfterTheEngineShutsDown()
    {
        var code = Source("App.xaml.cs");
        var shutdown = code.IndexOf("CefConfigurator.Shutdown();", StringComparison.Ordinal);
        var remove = code.IndexOf("ProfileCleanup.RemoveGpuCaches(AppPaths.Profile)", StringComparison.Ordinal);
        Assert.True(shutdown > 0 && remove > shutdown, "as caches da GPU têm de ser apagadas depois do CefConfigurator.Shutdown");
        Assert.Contains("ProfileCleanup.ShouldRemoveGpuCaches(Data.Settings.Current.ClearOnExit)", code);
    }

    // Uma janela privada (qualquer contexto dela, também o do Espreitar) e «Apagar dados › cache» marcam as caches para
    // apagar ao sair. O Espreitar de uma janela normal não: o que mostra não é privado.
    [Fact]
    public void PrivateWindowAndClearData_MarkThemForExit()
    {
        Assert.Matches(@"private IRequestContext CreatePrivateContext\([\s\S]*?if \(IsPrivate\) Infrastructure\.ProfileCleanup\.RemoveGpuCachesAtExit\(\);[\s\S]*?return context;", Source("MainWindow.Private.cs"));
        Assert.Matches(@"if \(request\.Cache\) Infrastructure\.ProfileCleanup\.RemoveGpuCachesAtExit\(\);", Source(Path.Combine("Browser", "EterisSchemeHandlerFactory.cs")));
    }

    // No arranque só com o ajuste ligado (regra R-2: nada de novo antes da 1.ª página sem a rede de segurança).
    [Fact]
    public void Startup_RemovesThemOnlyWithTheTweak()
    {
        var code = Source(Path.Combine("Infrastructure", "CefConfigurator.cs"));
        Assert.Contains("var shaderCache = PerfTuning.IsOn(PerfTuning.ShaderCache);", code);
        Assert.Contains("if (shaderCache) ProfileCleanup.RemoveGpuCaches(AppPaths.Profile);", code);
        Assert.Matches(@"Build\([^;]*shaderCache\);", code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PrivacyPage_SaysWhatHappensToTheGpuCache(bool on)
    {
        var args = new Dictionary<string, string>(CefConfigurator.Build("p", shaderCache: on).CefCommandLineArgs);
        var state = new PrivacyPage.State(args, PrivacySignals.Headers, GpcScript: true, ThirdPartyCookiesBlocked: true, WebRtcProtected: true,
            SecureDns: null, Language: "pt-PT", HideFromCapture: false, VisibleWindows: 1, HiddenWindows: 0);
        var promise = Assert.Single(PrivacyPage.Promises(state), p => p.Id == "gpu-cache");
        Assert.True(promise.Kept);
        if (on) Assert.Contains("janela privada", promise.Detail);
        else Assert.Contains("não ficam no disco", promise.Detail);
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
