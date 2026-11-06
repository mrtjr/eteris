using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

public class CosmeticScriptsTests
{
    [Fact]
    public void Inject_EmbedsCssAsEscapedJsonString()
    {
        var script = CosmeticScripts.Inject(".ad{display:none!important}\n");
        Assert.Contains("\".ad{display:none!important}\\n\"", script);
        Assert.Contains("data-eteris-shield", script);
    }

    [Fact]
    public void Inject_CssCannotBreakOutOfTheScript()
    {
        var script = CosmeticScripts.Inject("a[title=\"x\"]{}\"); alert(1); (\"</script>");
        Assert.DoesNotContain("\"); alert(1)", script);
        Assert.DoesNotContain("</script>", script);
    }

    [Fact]
    public void ParseClassesAndIds_SplitsTheTwoLines()
    {
        var (classes, ids) = CosmeticScripts.ParseClassesAndIds("banner ad-slot artigo\ntopo rodape");
        Assert.Equal(["banner", "ad-slot", "artigo"], classes);
        Assert.Equal(["topo", "rodape"], ids);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("so-classes")]
    public void ParseClassesAndIds_ToleratesPartialResults(string raw)
    {
        var (classes, ids) = CosmeticScripts.ParseClassesAndIds(raw);
        Assert.NotNull(classes);
        Assert.NotNull(ids);
    }
}
