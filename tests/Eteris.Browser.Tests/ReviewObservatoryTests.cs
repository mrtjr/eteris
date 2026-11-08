using System.IO;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

// Revisão aos 50 — Observatório, regras por site e mapa de empresas.
public class ReviewObservatoryTests
{
    private static DomainStat Domain(string host, int requests = 1) => new(host, requests, 0, [], ObservedVerdict.Allowed, null, 0);

    // «Deixar de bloquear» num subdomínio apanhado pela regra do domínio-pai desfaz essa regra (antes não fazia nada).
    [Fact]
    public void Unblock_ASubdomain_RemovesTheParentRuleThatCatchesIt()
    {
        var removed = new List<(string, string)>();
        var rules = new SiteRuleSet(new SiteBlockRules(), persistRemove: (s, d) => removed.Add((s, d)));
        rules.Block("jornal.pt", "tracker.com", isPrivate: false);
        Assert.True(rules.Blocks("jornal.pt", "cdn.tracker.com", false));
        var undone = rules.Unblock("jornal.pt", "cdn.tracker.com", false);
        Assert.Equal(["tracker.com"], undone.Rules);
        Assert.False(undone.SavedKept);
        Assert.False(rules.Blocks("jornal.pt", "cdn.tracker.com", false));
        Assert.False(rules.Blocks("jornal.pt", "tracker.com", false));
        Assert.Equal([("jornal.pt", "tracker.com")], removed);
        Assert.Equal("tracker.com e os subdomínios voltam a ser decididos pelas listas em jornal.pt",
            ObservatoryText.UnblockToast("jornal.pt", "cdn.tracker.com", undone));
    }

    [Fact]
    public void Unblock_UndoesEveryRuleThatCatchesTheHost_AndUndoPutsThemBack()
    {
        var rules = new SiteRuleSet(new SiteBlockRules());
        rules.Block("jornal.pt", "tracker.com", false);
        rules.Block("jornal.pt", "cdn.tracker.com", false);
        rules.Block("jornal.pt", "outro.com", false);
        var undone = rules.Unblock("jornal.pt", "img.cdn.tracker.com", false);
        Assert.Equal(["cdn.tracker.com", "tracker.com"], undone.Rules);
        Assert.False(rules.Blocks("jornal.pt", "img.cdn.tracker.com", false));
        Assert.True(rules.Blocks("jornal.pt", "outro.com", false));
        foreach (var rule in undone.Rules) rules.Block("jornal.pt", rule, false);
        Assert.True(rules.Blocks("jornal.pt", "img.cdn.tracker.com", false));
        Assert.Equal([("jornal.pt", "cdn.tracker.com"), ("jornal.pt", "outro.com"), ("jornal.pt", "tracker.com")], rules.All());
    }

    // Numa janela privada, a regra guardada fica suspensa só nesta sessão: o botão tem efeito e o disco não muda.
    [Fact]
    public void Unblock_InAPrivateWindow_SuspendsASavedRuleForThePrivateSessionOnly()
    {
        var removed = new List<(string, string)>();
        var rules = new SiteRuleSet(new SiteBlockRules(), persistRemove: (s, d) => removed.Add((s, d)));
        rules.Block("jornal.pt", "cdn.exemplo.com", isPrivate: false);
        Assert.True(rules.Blocks("jornal.pt", "cdn.exemplo.com", isPrivate: true));
        var undone = rules.Unblock("jornal.pt", "cdn.exemplo.com", isPrivate: true);
        Assert.Equal(["cdn.exemplo.com"], undone.Rules);
        Assert.True(undone.SavedKept);
        Assert.False(rules.Blocks("jornal.pt", "cdn.exemplo.com", isPrivate: true));
        Assert.True(rules.Blocks("jornal.pt", "cdn.exemplo.com", isPrivate: false));
        Assert.Empty(removed);
        Assert.Equal("cdn.exemplo.com volta a ser decidido pelas listas em jornal.pt nesta sessão privada (a regra continua nas janelas normais)",
            ObservatoryText.UnblockToast("jornal.pt", "cdn.exemplo.com", undone));
        // Anular: a regra guardada volta a valer na janela privada, sem uma cópia privada.
        rules.Block("jornal.pt", "cdn.exemplo.com", isPrivate: true);
        Assert.True(rules.Blocks("jornal.pt", "cdn.exemplo.com", isPrivate: true));
        rules.Unblock("jornal.pt", "cdn.exemplo.com", isPrivate: true);
        // O fim da sessão privada esquece a suspensão.
        rules.ForgetPrivate();
        Assert.True(rules.Blocks("jornal.pt", "cdn.exemplo.com", isPrivate: true));
    }

    [Fact]
    public void PrivateRules_StayInThePrivateSession()
    {
        var added = new List<(string, string)>();
        var rules = new SiteRuleSet(new SiteBlockRules(), persistAdd: (s, d) => added.Add((s, d)));
        Assert.True(rules.IsEmpty(true));
        rules.Block("jornal.pt", "tracker.com", isPrivate: true);
        Assert.True(rules.IsEmpty(false));
        Assert.False(rules.IsEmpty(true));
        Assert.True(rules.Blocks("jornal.pt", "a.tracker.com", true));
        Assert.False(rules.Blocks("jornal.pt", "a.tracker.com", false));
        Assert.Empty(added);
        var undone = rules.Unblock("jornal.pt", "a.tracker.com", true);
        Assert.Equal(["tracker.com"], undone.Rules);
        Assert.False(undone.SavedKept);
        Assert.False(rules.Blocks("jornal.pt", "a.tracker.com", true));
    }

    [Fact]
    public void Forget_DropsTheSiteRulesAndSuspensions()
    {
        var forgotten = new List<string>();
        var rules = new SiteRuleSet(new SiteBlockRules(), persistRemoveSite: forgotten.Add);
        rules.Block("jornal.pt", "tracker.com", false);
        rules.Unblock("jornal.pt", "tracker.com", true);
        rules.Forget("jornal.pt");
        Assert.Empty(rules.All());
        Assert.Equal(["jornal.pt"], forgotten);
        rules.Block("jornal.pt", "tracker.com", false);
        Assert.True(rules.Blocks("jornal.pt", "tracker.com", true));
    }

    [Fact]
    public void RulesFor_GoesFromNarrowToBroad()
    {
        var set = new SiteBlockRules();
        set.Add("a.pt", "x.com");
        set.Add("a.pt", "b.x.com");
        Assert.Equal(["b.x.com", "x.com"], set.RulesFor("a.pt", "c.b.x.com."));
        Assert.Empty(set.RulesFor("a.pt", "y.com"));
        Assert.Empty(set.RulesFor("b.pt", "x.com"));
        Assert.True(set.Contains("a.pt", "X.com"));
    }

    // O mapa de empresas não culpa quem só aluga o servidor (CDN, alojamento) pelo que os clientes lá servem.
    [Theory]
    [InlineData("d1a2b3.cloudfront.net", null)]
    [InlineData("bucket.s3.amazonaws.com", null)]
    [InlineData("x.azureedge.net", null)]
    [InlineData("conta.blob.core.windows.net", null)]
    [InlineData("lh3.googleusercontent.com", null)]
    [InlineData("app.firebaseapp.com", null)]
    [InlineData("blog.blogspot.com", null)]
    [InlineData("a1.akamaihd.net", null)]
    [InlineData("www.edgekey.net", null)]
    [InlineData("global.fastly.net", null)]
    [InlineData("raw.githubusercontent.com", null)]
    [InlineData("loja.myshopify.com", null)]
    [InlineData("site.b-cdn.net", null)]
    [InlineData("aax.amazon-adsystem.com", "Amazon")]
    [InlineData("www.google-analytics.com", "Google")]
    [InlineData("c.clarity.ms", "Microsoft")]
    [InlineData("s.go-mpulse.net", "Akamai")]
    [InlineData("www.fastly-insights.com", "Fastly")]
    public void CompanyMap_LeavesSharedHostingOut(string host, string? owner)
        => Assert.Equal(owner, CompanyMap.Default.OwnerOf(host));

    // Com mais domínios do que cabem no anel, o painel diz quantos ficaram só na lista.
    [Fact]
    public void HiddenNodes_AreAnnounced()
    {
        var domains = Enumerable.Range(0, 70).Select(i => Domain($"d{i}.exemplo{i}.com")).ToList();
        var drawing = ObservatoryLayout.Place([new CompanyGroup("Outros", false, domains)], 440);
        Assert.Equal(22, drawing.Hidden);
        Assert.Equal("+22 domínios só na lista", ObservatoryText.HiddenNote(drawing.Hidden));
        Assert.Equal("+1 domínio só na lista", ObservatoryText.HiddenNote(1));
        Assert.Null(ObservatoryText.HiddenNote(0));
        var source = Read("src", "Eteris.Browser", "MainWindow.Observatory.cs");
        Assert.Contains("ObservatoryText.HiddenNote(drawing.Hidden)", source);
    }

    // Fechar a janela com o Observatório aberto para o temporizador de 250 ms (prendia a janela inteira).
    [Fact]
    public void ClosingTheWindow_StopsTheObservatoryTimer()
    {
        var source = Read("src", "Eteris.Browser", "MainWindow.Observatory.cs");
        Assert.Contains("Closed += (_, _) => StopObservatoryTimer();", source);
        Assert.Contains("SiteRules.Unblock(site, host, isPrivate)", source);
        Assert.Contains("ObservatoryText.UnblockToast(site, host, undone)", source);
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root(), .. parts]));

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
