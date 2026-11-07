using System.IO;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Tabs;
using Eteris.Browser.Theme;

namespace Eteris.Browser.Tests;

// Resíduos da revisão do bloco A: os controlos ligados das páginas internas seguem a Cor do Éteris e a paleta
// conta as abas de todos os Espaços da janela fechada.
public class ReviewBlockAResidueTests
{
    private static readonly string[] Pages = ["settings", "welcome", "bookmarks", "history", "whatsnew", "error", "newtab"];

    private static string Read(string page)
    {
        using var stream = typeof(InternalPages).Assembly.GetManifestResourceStream($"Eteris.Browser.Browser.{page}.html")!;
        return new StreamReader(stream).ReadToEnd();
    }

    // O par íris fixo (claro #c4b5fd→#67e8f9 e forte #8b5cf6→#22d3ee) não volta a aparecer nos controlos.
    [Theory]
    [MemberData(nameof(PageNames))]
    public void InternalPages_HaveNoFixedIrisGradientOnControls(string page)
    {
        var html = Read(page);
        Assert.DoesNotContain("linear-gradient(135deg, #c4b5fd, #67e8f9)", html);
        Assert.DoesNotContain("linear-gradient(135deg, #8b5cf6, #22d3ee)", html);
    }

    public static TheoryData<string> PageNames()
    {
        var data = new TheoryData<string>();
        foreach (var page in Pages) data.Add(page);
        return data;
    }

    [Fact]
    public void Settings_SwitchesAndPills_UseTheAccentVariables()
    {
        var html = Read("settings");
        Assert.Contains("--accent: #8b5cf6; --accent-2: #22d3ee;", html);
        Assert.Contains(".switch input:checked + i { background: linear-gradient(135deg, var(--accent), var(--accent-2)); }", html);
        Assert.Matches(@"\.seg button\.on[^{]*\{[^}]*var\(--accent\)[^}]*var\(--accent-2\)", html);
    }

    [Fact]
    public void AccentStyle_CarriesTheSecondTone()
    {
        var coral = Accent.Gradient(Accent.PaletteColor("coral")!.Value);
        Assert.Contains("--accent-2: " + Accent.Hex(coral[1]), InternalPages.AccentStyle(coral));
        Assert.Contains("setProperty('--accent-2', '" + Accent.Hex(coral[1]) + "')", InternalPages.AccentScript(coral));
        Assert.Contains("removeProperty('--accent-2')", InternalPages.AccentScript(Accent.IrisStops));
    }

    [Fact]
    public void ClosedWindow_CountsTheTabsOfEverySpace()
    {
        var work = new SessionSpace("Trabalho", 2, null, new Session(["https://a.pt/", "https://b.pt/", "https://c.pt/"], 0));
        var empty = new SessionSpace("Vazio", 3, null, new Session([], 0));
        var window = new ClosedWindow(["https://x.pt/"], ["X"], 0, [new SessionSpace("Geral", 0), work, empty], 0);
        Assert.Equal(4, window.TabCount);
        Assert.Equal(2, window.SpacesWithTabs);
        Assert.Equal("Reabrir janela fechada (4 abas)", window.ReopenLabel);
    }

    [Fact]
    public void ClosedWindow_WithoutSpaces_CountsTheStrip()
    {
        var window = new ClosedWindow(["https://x.pt/"], ["X"], 0);
        Assert.Equal(1, window.TabCount);
        Assert.Equal(1, window.SpacesWithTabs);
        Assert.Equal("Reabrir janela fechada (1 aba)", window.ReopenLabel);
    }

    // Só os outros Espaços contam pela reserva: o Espaço à vista tem as abas na faixa.
    [Fact]
    public void ClosedWindow_FromTheSecondSpace_DoesNotCountTheCurrentTwice()
    {
        var general = new SessionSpace("Geral", 0, null, new Session(["https://g.pt/"], 0));
        var window = new ClosedWindow(["https://w1.pt/", "https://w2.pt/"], ["W1", "W2"], 0, [general, new SessionSpace("Trabalho", 1)], 1);
        Assert.Equal(3, window.TabCount);
        Assert.Equal(2, window.SpacesWithTabs);
    }
}
