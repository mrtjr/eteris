using System.IO;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

public class ScriptletTests
{
    // base64 de: function testMark(value) { window.__eterisTest = value; }
    private const string Resources = """[{"name":"test-mark.js","aliases":["tm.js"],"kind":{"mime":"application/javascript"},"content":"ZnVuY3Rpb24gdGVzdE1hcmsodmFsdWUpIHsgd2luZG93Ll9fZXRlcmlzVGVzdCA9IHZhbHVlOyB9","dependencies":[]}]""";

    [Fact]
    public void Engine_ReturnsScriptletsForMatchingSite()
    {
        using var engine = AdblockEngine.Create("publico.pt##+js(tm, ola)\n", Resources);
        var script = engine.Scriptlets("https://www.publico.pt/");
        Assert.Contains("testMark", script);
        Assert.Contains("ola", script);
        Assert.Equal("", engine.Scriptlets("https://www.expresso.pt/"));
    }

    [Fact]
    public void Engine_WithoutResources_HasNoScriptlets()
    {
        using var engine = AdblockEngine.Create("publico.pt##+js(tm, ola)\n");
        Assert.Equal("", engine.Scriptlets("https://www.publico.pt/"));
    }

    [Fact]
    public void Engine_BundledUBlockScriptletsAreValid()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Shields", "resources", "scriptlets.json");
        using var engine = AdblockEngine.Create("mynet.com##+js(nostif, googlefc)\n", File.ReadAllText(bundled));
        Assert.Contains("googlefc", engine.Scriptlets("https://www.mynet.com/"));
    }

    [Fact]
    public void Wrap_RunsOnlyInTopFrameOfTheSameHost()
    {
        var wrapped = ScriptletScripts.Wrap("testMark(\"ola\")", "https://www.publico.pt/artigo");
        Assert.Contains("window.top === window", wrapped);
        Assert.Contains("location.hostname === \"www.publico.pt\"", wrapped);
        Assert.Contains("testMark(\"ola\")", wrapped);
        Assert.Contains("catch", wrapped);
    }

    [Fact]
    public void Wrap_DefinesTheGlobalsUBlockScriptletsExpect()
    {
        var wrapped = ScriptletScripts.Wrap("testMark(\"ola\")", "https://www.publico.pt/")!;
        Assert.Contains("const scriptletGlobals = {};", wrapped);
        Assert.True(wrapped.IndexOf("scriptletGlobals", StringComparison.Ordinal) < wrapped.IndexOf("testMark", StringComparison.Ordinal));
    }

    [Fact]
    public void Wrap_EmptyScriptOrNonWebAddress_IsNull()
    {
        Assert.Null(ScriptletScripts.Wrap("", "https://www.publico.pt/"));
        Assert.Null(ScriptletScripts.Wrap("x()", "eteris://newtab/"));
    }

    [Fact]
    public void Wrap_HostIsEscaped()
        => Assert.DoesNotContain("\"x\"; alert(1)", ScriptletScripts.Wrap("x()", "https://a.pt/") ?? "");
}
