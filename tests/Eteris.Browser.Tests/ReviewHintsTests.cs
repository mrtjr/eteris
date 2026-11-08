using System.IO;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

// Revisão aos 50 — a promessa «dicas do cliente mínimas» também no JavaScript (navigator.userAgentData).
public class ReviewHintsTests
{
    // As dicas do cliente de alta entropia dão valores genéricos (como nos cabeçalhos), reduzidas no próprio motor por
    // aba (Emulation.setUserAgentOverride com metadados), para a página e os workers dela darem o mesmo (medido,
    // docs/paridade.md #6: antes, só a página era reduzida por script e o worker dava 19.0.0 e 156.0.8078.80).
    [Fact]
    public void HighEntropyHints_AreGeneric_ForThePageAndItsWorkers()
    {
        var m = ReducedClientHints.Metadata(156);
        Assert.Equal("10.0.0", m.PlatformVersion);
        Assert.Equal("156.0.0.0", m.FullVersion);
        Assert.Equal("x86", m.Architecture);
        Assert.Equal("64", m.Bitness);
        Assert.Equal(string.Empty, m.Model);
        Assert.False(m.Mobile);
        Assert.False(m.Wow64);
        Assert.Equal("Windows", m.Platform);
        Assert.Equal(["Desktop"], m.FormFactors);
        // As marcas que o motor 156 do Éteris dá de facto (medidas): GREASE e ordem do Chromium, sem «Google Chrome».
        Assert.Equal("Not:A-Brand/8, Chromium/156", string.Join(", ", m.Brands.Select(b => b.Brand + "/" + b.Version)));
        Assert.Equal("Not:A-Brand/8.0.0.0, Chromium/156.0.0.0", string.Join(", ", m.FullVersionList.Select(b => b.Brand + "/" + b.Version)));
        // Sem script na página: nenhuma função trocada que o CreepJS apanhe.
        Assert.DoesNotContain("getHighEntropyValues", PrivacySignals.SessionScript);
        Assert.StartsWith(PrivacySignals.GpcScript, PrivacySignals.SessionScript);
        var injector = Read("src", "Eteris.Browser", "Shields", "ScriptletInjector.cs");
        Assert.Contains("AddScriptToEvaluateOnNewDocumentAsync(PrivacySignals.SessionScript)", injector);
        // Antes do onSession: a vista móvel de um site fixado põe os metadados dela por cima.
        Assert.True(injector.IndexOf("ReducedClientHints.Apply(created)", StringComparison.Ordinal)
                    < injector.IndexOf("onSession(created)", StringComparison.Ordinal));
        Assert.Contains("hints: () =>", Read("src", "Eteris.Browser", "Browser", "privacy.html"));
    }

    // O GREASE muda com a versão maior, como no Chromium (157: «Not-A.Brand»/99, medido no Brave 157).
    [Theory]
    [InlineData(156, "Not:A-Brand/8, Chromium/156")]
    [InlineData(157, "Chromium/157, Not-A.Brand/99")]
    public void Brands_FollowTheChromiumGrease(int major, string expected)
        => Assert.Equal(expected, string.Join(", ", ReducedClientHints.Brands(major, fullVersion: false).Select(b => b.Brand + "/" + b.Version)));

    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/156.0.0.0 Safari/537.36", 156)]
    [InlineData("Mozilla/5.0 Firefox/159.0", null)]
    [InlineData(null, null)]
    public void MajorOf_ReadsTheChromiumVersion(string? userAgent, int? expected) => Assert.Equal(expected, ReducedClientHints.MajorOf(userAgent));

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root(), .. parts]));

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
