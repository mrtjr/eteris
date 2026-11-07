using System.Text.Json;
using Eteris.Browser.Browser;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

public class PageScriptTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    [Fact]
    public void DevToolsValues_BecomeWhatTheCallersExpect()
    {
        Assert.Equal("playing", PageScript.ValueOf(Json("\"playing\"")));
        Assert.Equal(true, PageScript.ValueOf(Json("true")));
        Assert.Equal(false, PageScript.ValueOf(Json("false")));
        Assert.Equal(1234.5, PageScript.ValueOf(Json("1234.5")));
        Assert.Null(PageScript.ValueOf(Json("null")));
        Assert.Null(PageScript.ValueOf(null));
        Assert.Equal("{\"a\":1}", PageScript.ValueOf(Json("{\"a\":1}")));
    }

    [Fact]
    public void PlainValues_AreKept_AndNumbersBecomeDouble()
    {
        Assert.Equal("x", PageScript.ValueOf("x"));
        Assert.Equal(true, PageScript.ValueOf(true));
        Assert.Equal(42d, PageScript.ValueOf(42));
        Assert.Equal(7d, PageScript.ValueOf(7L));
    }

    [Fact]
    public void AsyncBody_IsWrappedInAnAsyncFunction()
    {
        Assert.Equal("1 + 1", PageScript.Expression("1 + 1", asyncBody: false));
        var wrapped = PageScript.Expression("return await f();", asyncBody: true);
        Assert.StartsWith("(async () => {", wrapped);
        Assert.EndsWith("})()", wrapped);
        Assert.Contains("return await f();", wrapped);
    }

    [Fact]
    public void EarlyCosmeticCss_OnlyInTheMainFrame_AndWaitsForTheDocument()
    {
        var script = CosmeticScripts.InjectEarly(".anuncio { display: none !important; }");
        Assert.Contains("window.top !== window", script);
        Assert.Contains("MutationObserver", script);
        Assert.Contains("data-eteris-shield", script);
        Assert.Equal(string.Empty, CosmeticScripts.InjectEarly(string.Empty));
    }
}
