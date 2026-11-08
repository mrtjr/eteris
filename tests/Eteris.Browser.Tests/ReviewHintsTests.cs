using System.IO;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

// Revisão aos 50 — a promessa «dicas do cliente mínimas» também no JavaScript (navigator.userAgentData).
public class ReviewHintsTests
{
    // As dicas do cliente de alta entropia no JavaScript dão valores genéricos (como nos cabeçalhos), no mesmo
    // registo do GPC, antes de qualquer script da página.
    [Fact]
    public void HighEntropyHints_AreGenericInJavaScript()
    {
        var script = PrivacySignals.HintsScript;
        Assert.Contains("NavigatorUAData", script);
        Assert.Contains("getHighEntropyValues", script);
        Assert.Contains("original.call(this, hints)", script);
        foreach (var hint in new[] { "'architecture'", "'bitness'", "'model'", "'platformVersion'", "'uaFullVersion'", "'fullVersionList'", "'wow64'" })
            Assert.Contains(hint, script);
        Assert.Contains("'10.0.0'", script);
        Assert.Contains("'.0.0.0'", script);
        Assert.StartsWith(PrivacySignals.GpcScript, PrivacySignals.SessionScript);
        Assert.Contains(PrivacySignals.HintsScript, PrivacySignals.SessionScript);
        Assert.Contains("AddScriptToEvaluateOnNewDocumentAsync(PrivacySignals.SessionScript)", Read("src", "Eteris.Browser", "Shields", "ScriptletInjector.cs"));
        Assert.Contains("hints: () =>", Read("src", "Eteris.Browser", "Browser", "privacy.html"));
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root(), .. parts]));

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
