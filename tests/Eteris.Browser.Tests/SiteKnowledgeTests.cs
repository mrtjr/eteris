using System.IO;
using CefSharp;
using Eteris.Browser.Browser;
using Eteris.Browser.Data;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

// B9 — bloquear um domínio só num site, «Porque é que isto passou?» e o cartão «O que este site sabe de ti».
public class SiteKnowledgeTests
{
    [Fact]
    public void SiteRule_CoversTheDomainAndItsSubdomains_OnlyInThatSite()
    {
        var rules = new SiteBlockRules();
        Assert.True(rules.IsEmpty);
        Assert.False(rules.Blocks("jornal.pt", "www.google-analytics.com"));
        Assert.True(rules.Add("jornal.pt", "Google-Analytics.com."));
        Assert.False(rules.Add("jornal.pt", "google-analytics.com"));
        Assert.True(rules.Blocks("jornal.pt", "www.google-analytics.com"));
        Assert.True(rules.Blocks("jornal.pt", "google-analytics.com"));
        Assert.False(rules.Blocks("jornal.pt", "notgoogle-analytics.com"));
        Assert.False(rules.Blocks("outro.pt", "www.google-analytics.com"));
        Assert.Equal(["google-analytics.com"], rules.ForSite("jornal.pt"));
        Assert.True(rules.Remove("jornal.pt", "google-analytics.com"));
        Assert.True(rules.IsEmpty);
        rules.Add("a.pt", "x.example");
        rules.Add("a.pt", "y.example");
        rules.Add("b.pt", "x.example");
        rules.RemoveSite("a.pt");
        Assert.Equal([("b.pt", "x.example")], rules.All());
        rules.Clear();
        Assert.True(rules.IsEmpty);
    }

    [Fact]
    public void SiteRuleStore_KeepsTheRulesOnDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eteris-rules-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SiteRuleStore(path);
            store.Add("jornal.pt", "connect.facebook.net");
            store.Add("jornal.pt", "connect.facebook.net");
            store.Add("loja.pt", "static.criteo.net");
            Assert.Equal([("jornal.pt", "connect.facebook.net"), ("loja.pt", "static.criteo.net")], new SiteRuleStore(path).All());
            store.Remove("jornal.pt", "connect.facebook.net");
            store.RemoveSite("loja.pt");
            Assert.Empty(new SiteRuleStore(path).All());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    // A regra do utilizador é consultada antes das listas e o pedido aparece âmbar no Observatório.
    [Fact]
    public void Handler_AppliesTheUserRuleBeforeTheLists()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "Shields", "PrivacyRequestHandler.cs"));
        var rule = source.IndexOf("SiteRules.Blocks(pageUrl, request.Url, isPrivate)", StringComparison.Ordinal);
        Assert.True(rule > 0);
        Assert.True(rule < source.IndexOf("blocker().ShouldBlock(request.Url, documentUrl, adblockType)", StringComparison.Ordinal));
        Assert.Contains("ObservedVerdict.UserBlocked", source[rule..]);
        // «Repor» o site e o fim da sessão privada levam as regras.
        Assert.Contains("Shields.SiteRules.Forget(site);", File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "Data", "AppData.cs")));
        Assert.Equal(1, CountOf(File.ReadAllText(Path.Combine(Root(), "src", "Eteris.Browser", "Tabs", "PrivateSession.cs")), "SiteRules.ForgetPrivate()"));
    }

    private static int CountOf(string text, string what)
    {
        var n = 0;
        for (var i = text.IndexOf(what, StringComparison.Ordinal); i >= 0; i = text.IndexOf(what, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    private static DomainStat Domain(ObservedVerdict verdict, params ObservedKind[] kinds)
    {
        var o = new SiteObservatory();
        o.Reset("https://www.jornal.pt/");
        foreach (var kind in kinds) o.Record("https://cdn.terceiro.example/x", kind, verdict);
        return Assert.Single(o.Snapshot().ThirdParties);
    }

    [Fact]
    public void WhyAllowed_ExplainsTheReasonInPlainWords()
    {
        var known = SiteObservatory.WhyAllowed(Domain(ObservedVerdict.Allowed, ObservedKind.Script), "Google", listsKnowIt: true, thirdPartyCookiesBlocked: true);
        Assert.StartsWith("As listas conhecem-no como rastreador, mas uma regra de exceção", known);
        Assert.Contains("não recebe nem guarda cookies", known);
        Assert.EndsWith("bloqueie-o só neste site.", known);
        Assert.StartsWith("Este domínio de Google serve conteúdo incorporado",
            SiteObservatory.WhyAllowed(Domain(ObservedVerdict.Allowed, ObservedKind.Frame), "Google", false, true));
        Assert.StartsWith("Este domínio só entrega fontes, estilos ou imagens",
            SiteObservatory.WhyAllowed(Domain(ObservedVerdict.Allowed, ObservedKind.Font, ObservedKind.Style), null, false, true));
        Assert.StartsWith("Nenhuma regra das listas o apanha",
            SiteObservatory.WhyAllowed(Domain(ObservedVerdict.Allowed, ObservedKind.Xhr), null, false, true));
        Assert.Contains("pode guardar cookies", SiteObservatory.WhyAllowed(Domain(ObservedVerdict.Allowed, ObservedKind.Xhr), null, false, false));
        Assert.StartsWith("Desligou o Escudo neste site", SiteObservatory.WhyAllowed(Domain(ObservedVerdict.ShieldOff, ObservedKind.Script), null, true, true));
    }

    [Fact]
    public void RuleToast_SaysWhereAndForHowLong()
    {
        Assert.Equal("connect.facebook.net bloqueado em jornal.pt", ObservatoryText.RuleToast("jornal.pt", "connect.facebook.net", true, false));
        Assert.Equal("connect.facebook.net bloqueado em jornal.pt (até terminar a sessão privada)", ObservatoryText.RuleToast("jornal.pt", "connect.facebook.net", true, true));
        Assert.Equal("connect.facebook.net volta a ser decidido pelas listas em jornal.pt", ObservatoryText.RuleToast("jornal.pt", "connect.facebook.net", false, false));
    }

    [Theory]
    [InlineData(0.5, "menos de 1 hora")]
    [InlineData(5, "5 horas")]
    [InlineData(24, "1 dia")]
    [InlineData(24 * 12, "12 dias")]
    [InlineData(24 * 95, "3 meses")]
    [InlineData(24 * 400, "1 ano e 1 mês")]
    [InlineData(24 * 731, "2 anos")]
    public void Duration_InPlainWords(double hours, string expected)
        => Assert.Equal(expected, SiteKnowledge.Duration(TimeSpan.FromHours(hours)));

    [Theory]
    [InlineData(1, "1 byte")]
    [InlineData(800, "800 bytes")]
    [InlineData(340 * 1024, "340 KB")]
    [InlineData(1258291, "1,2 MB")]
    public void Size_InPortuguese(long bytes, string expected) => Assert.Equal(expected, SiteKnowledge.Size(bytes));

    [Fact]
    public void Card_TellsWhatTheSiteKnows()
    {
        var now = new DateTime(2026, 11, 7, 12, 0, 0);
        var lines = SiteKnowledge.Describe(new(
            [now.AddDays(400), now.AddDays(30), null], 1258291, ThirdParties: 21, Companies: 9, ThirdPartiesBlocked: 15,
            ReferrerToOthers: "https://www.jornal.pt/", LocalNetworkAttempts: 3, now));
        Assert.Equal(5, lines.Count);
        Assert.Equal("3 cookies guardados", lines[0].Title);
        Assert.Equal("O mais duradouro fica 1 ano e 1 mês; 1 só até fechar o Éteris.", lines[0].Detail);
        Assert.Equal("≈ 1,2 MB guardados", lines[1].Title);
        Assert.Equal("Falou com 21 domínios de outros sites", lines[2].Title);
        Assert.Equal("De 9 empresas conhecidas · 15 bloqueados — veja-os no Observatório.", lines[2].Detail);
        Assert.Contains("«https://www.jornal.pt/»", lines[3].Detail);
        Assert.Equal("Tentou 3 vezes falar com a sua rede local", lines[4].Title);

        var quiet = SiteKnowledge.Describe(new([], 0, 0, 0, 0, null, 0, now));
        Assert.Equal(["Nenhum cookie guardado", "Nada guardado no armazenamento", "Nenhum terceiro contactado", "Referrer enviado: nada",
            "Não tentou falar com a sua rede local"], quiet.Select(l => l.Title));
        Assert.Equal("Todos só até fechar o Éteris.", SiteKnowledge.Describe(new([null, null], null, 0, 0, 0, null, 0, now))[0].Detail);
        Assert.Equal("Armazenamento: por medir", SiteKnowledge.Describe(new([], null, 0, 0, 0, null, 0, now))[1].Title);
    }

    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
