using System.IO;
using CefSharp;
using Eteris.Browser.Data;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

// B7 (parte 2) «Esquecer os sites de salto»: cadeias de navegação e prazo com relógio injetado.
public class BounceTrackingTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);

    private static string[] Sites(IReadOnlyList<BounceSite> bounces) => bounces.Select(b => b.Site).ToArray();

    [Fact]
    public void ServerBounceBetweenTwoSites_MarksTheMiddleSite()
    {
        var chain = new RedirectChain();
        chain.Begin("https://jornal.pt/artigo", "https://t.rastreador.com/c?id=1", T0);
        chain.Continue(null, "https://loja.pt/promo", T0.AddMilliseconds(200), serverRedirect: true, rewritten: false);
        Assert.Equal(["rastreador.com"], Sites(chain.Close()));
    }

    [Fact]
    public void ClientBounceWithoutGesture_IsAlsoABounce()
    {
        var chain = new RedirectChain();
        chain.Begin("https://jornal.pt/", "https://clk.rastreador.com/x", T0);
        chain.Continue("https://clk.rastreador.com/x", "https://sync.outro-rastreador.net/s", T0.AddSeconds(1), serverRedirect: false, rewritten: false);
        chain.Continue("https://sync.outro-rastreador.net/s", "https://loja.pt/", T0.AddSeconds(2), serverRedirect: false, rewritten: false);
        Assert.Equal(["rastreador.com", "outro-rastreador.net"], Sites(chain.Close()));
    }

    [Fact]
    public void RoundTripToTheStartingSite_IsALoginNotABounce()
    {
        var chain = new RedirectChain();
        chain.Begin("https://loja.pt/entrar", "https://accounts.google.com/o/oauth2/auth", T0);
        chain.Continue(null, "https://loja.pt/conta", T0.AddSeconds(1), serverRedirect: true, rewritten: false);
        Assert.Empty(chain.Close());
    }

    [Fact]
    public void SameSiteHopsAndTheDestination_AreNeverBounces()
    {
        var chain = new RedirectChain();
        chain.Begin("https://jornal.pt/", "https://www.loja.pt/", T0);
        chain.Continue(null, "https://loja.pt/inicio", T0.AddSeconds(1), serverRedirect: true, rewritten: false);
        Assert.Empty(chain.Close());
        // Uma navegação só, sem passos pelo meio.
        chain.Begin("https://jornal.pt/", "https://loja.pt/", T0);
        Assert.Empty(chain.Close());
    }

    [Fact]
    public void AddressRewrittenByEteris_NeverCountsAsAHop()
    {
        // O salto de rastreio (google.com/url) foi trocado pelo destino antes de sair: o Google nunca carregou.
        var chain = new RedirectChain();
        chain.Begin("https://jornal.pt/", "https://www.google.com/url?q=https://loja.pt/", T0);
        chain.Continue(null, "https://loja.pt/", T0.AddMilliseconds(5), serverRedirect: true, rewritten: true);
        Assert.Equal(1, chain.Hops);
        Assert.Empty(chain.Close());
    }

    [Fact]
    public void ScriptNavigationAfterTheUserStayed_StartsANewChain()
    {
        var chain = new RedirectChain();
        chain.Begin("https://jornal.pt/", "https://forum.pt/", T0);
        var closed = chain.Continue("https://forum.pt/", "https://loja.pt/", T0 + RedirectChain.DwellLimit + TimeSpan.FromSeconds(1),
            serverRedirect: false, rewritten: false);
        Assert.Empty(closed);
        Assert.Empty(chain.Close());
    }

    [Fact]
    public void NewUserNavigation_ClosesThePreviousChain()
    {
        var chain = new RedirectChain();
        chain.Begin("https://a.pt/", "https://t.rastreador.com/", T0);
        chain.Continue(null, "https://b.pt/", T0.AddSeconds(1), serverRedirect: true, rewritten: false);
        Assert.Equal(["rastreador.com"], Sites(chain.Begin("https://b.pt/", "https://c.pt/", T0.AddMinutes(1))));
    }

    [Fact]
    public void LocalAddresses_AreNeverMarked()
    {
        var chain = new RedirectChain();
        chain.Begin("https://a.pt/", "http://192.168.1.1/login", T0);
        chain.Continue(null, "https://b.pt/", T0.AddSeconds(1), serverRedirect: true, rewritten: false);
        Assert.Empty(chain.Close());
    }

    [Theory]
    [InlineData(false, true, TransitionType.LinkClicked, true)]
    [InlineData(false, false, TransitionType.LinkClicked | TransitionType.ClientRedirect, true)]
    [InlineData(true, false, TransitionType.LinkClicked | TransitionType.ClientRedirect, false)]
    [InlineData(true, false, TransitionType.LinkClicked, false)]
    [InlineData(false, false, TransitionType.Explicit, false)]
    [InlineData(false, false, TransitionType.LinkClicked | TransitionType.ForwardBack | TransitionType.ClientRedirect, false)]
    public void Continues_OnlyWithoutTheUser(bool gesture, bool redirect, TransitionType transition, bool expected)
        => Assert.Equal(expected, RedirectChain.Continues(gesture, redirect, transition));

    [Fact]
    public void IsTyped_CoversTheAddressBarAndBookmarks()
    {
        Assert.True(RedirectChain.IsTyped(TransitionType.Explicit | TransitionType.FromApi));
        Assert.True(RedirectChain.IsTyped(TransitionType.AutoBookmark));
        Assert.False(RedirectChain.IsTyped(TransitionType.LinkClicked));
        Assert.False(RedirectChain.IsTyped(TransitionType.FormSubmit));
    }

    [Fact]
    public void Ledger_ClearsOnlyAfterTheGracePeriod()
    {
        var now = T0;
        var ledger = new BounceLedger(() => now);
        Assert.True(ledger.RecordBounce("rastreador.com"));
        Assert.Empty(ledger.Due());
        now = T0 + BounceLedger.Grace - TimeSpan.FromSeconds(1);
        Assert.Empty(ledger.Due());
        now = T0 + BounceLedger.Grace;
        Assert.Equal(["rastreador.com"], ledger.Due());
        Assert.Equal(T0 + BounceLedger.Grace, ledger.DueAt("rastreador.com"));
        ledger.MarkCleared("rastreador.com");
        Assert.Empty(ledger.Due());
        Assert.Equal(1, ledger.Cleared);
    }

    [Fact]
    public void Ledger_RealVisitsAreSparedFor45Days()
    {
        var now = T0;
        var ledger = new BounceLedger(() => now);
        ledger.RecordInteraction("loja.pt");
        Assert.False(ledger.RecordBounce("loja.pt"));
        now = T0 + BounceLedger.InteractionMemory + TimeSpan.FromDays(1);
        Assert.True(ledger.RecordBounce("loja.pt"));
        // Um clique depois do salto tira a marca.
        ledger.RecordInteraction("loja.pt");
        now += BounceLedger.Grace;
        Assert.Empty(ledger.Due());
    }

    [Fact]
    public void Ledger_OpenSitesWait()
    {
        var now = T0;
        var ledger = new BounceLedger(() => now);
        ledger.RecordBounce("rastreador.com");
        now += BounceLedger.Grace;
        Assert.Empty(ledger.Due(keep: site => site == "rastreador.com"));
        Assert.Equal(["rastreador.com"], ledger.Due());
    }

    [Fact]
    public void Ledger_FirstBounceSetsTheClock()
    {
        var now = T0;
        var ledger = new BounceLedger(() => now);
        ledger.RecordBounce("rastreador.com");
        now = T0.AddMinutes(50);
        ledger.RecordBounce("rastreador.com");
        now = T0 + BounceLedger.Grace;
        Assert.Equal(["rastreador.com"], ledger.Due());
    }

    [Fact]
    public void Ledger_SurvivesARestartAndForgetsOldVisits()
    {
        var now = T0;
        var ledger = new BounceLedger(() => now);
        ledger.RecordBounce("rastreador.com");
        ledger.RecordInteraction("loja.pt");
        Assert.True(ledger.Dirty);
        var json = ledger.Serialize();
        Assert.False(ledger.Dirty);

        var again = BounceLedger.Load(json, () => now);
        Assert.Equal("rastreador.com", Assert.Single(again.Pending).Key);
        Assert.False(again.RecordBounce("loja.pt"));

        now = T0 + BounceLedger.InteractionMemory;
        var later = BounceLedger.Load(json, () => now);
        Assert.True(later.RecordBounce("loja.pt"));
        Assert.Empty(BounceLedger.Load("{ estragado", () => now).Pending);
        Assert.Empty(BounceLedger.Load(null, () => now).Pending);
    }

    [Fact]
    public void Ledger_ForgetsWhatTheUserErases()
    {
        var now = T0;
        var ledger = new BounceLedger(() => now);
        ledger.RecordBounce("rastreador.com");
        ledger.RecordInteraction("loja.pt");
        ledger.Forget("rastreador.com");
        Assert.Empty(ledger.Pending);
        now = T0.AddMinutes(5);
        ledger.RecordInteraction("forum.pt");
        ledger.ForgetInteractionsSince(T0.AddMinutes(1));
        Assert.True(ledger.RecordBounce("forum.pt"));
        Assert.False(ledger.RecordBounce("loja.pt"));
    }

    [Fact]
    public void Texts_SayWhatHappensAndWhen()
    {
        Assert.Equal(string.Empty, BounceText.ShieldLine([], enabled: true));
        Assert.Equal("⚠  rastreador.com serviu de salto sem visita: os cookies e o armazenamento são apagados dentro de 1 hora",
            BounceText.ShieldLine(["rastreador.com"], enabled: true));
        Assert.StartsWith("·  a.com e mais 1 serviram de salto", BounceText.ShieldLine(["a.com", "b.com"], enabled: false));
        Assert.Equal("Apagámos os cookies e o armazenamento de rastreador.com, que só serviu de salto", BounceText.Toast(["rastreador.com"]));
        Assert.Contains("4 sites", BounceText.Toast(["a.com", "b.com", "c.com", "d.com"]));
    }

    [Fact]
    public void Setting_IsOnByDefaultAndCanBeTurnedOff()
    {
        Assert.True(new UserSettings().ForgetBounceTrackers);
        Assert.False(new UserSettings().With("bounceForget", "false")!.ForgetBounceTrackers);
    }

    // Revisão aos 50: o armazenamento apaga-se por origem, e um rastreador guarda-o no subdomínio por onde o salto
    // passou (clk.rastreador.com), não em rastreador.com nem em www.rastreador.com.
    [Fact]
    public void Close_KeepsTheOriginsEachBounceWentThrough()
    {
        var chain = new RedirectChain();
        chain.Begin("https://jornal.pt/", "https://clk.rastreador.com/x?id=1", T0);
        chain.Continue(null, "https://sync.rastreador.com:8443/s", T0.AddSeconds(1), serverRedirect: false, rewritten: false);
        chain.Continue(null, "http://CLK.rastreador.com/y", T0.AddSeconds(2), serverRedirect: true, rewritten: false);
        chain.Continue(null, "https://loja.pt/", T0.AddSeconds(3), serverRedirect: true, rewritten: false);
        var bounce = Assert.Single(chain.Close());
        Assert.Equal("rastreador.com", bounce.Site);
        Assert.Equal(["http://clk.rastreador.com", "https://clk.rastreador.com", "https://sync.rastreador.com:8443"], bounce.Origins);
    }

    [Fact]
    public void Ledger_RemembersTheOriginsUntilTheSiteIsCleared()
    {
        var now = T0;
        var ledger = new BounceLedger(() => now);
        ledger.RecordBounce("rastreador.com", ["https://clk.rastreador.com"]);
        ledger.RecordBounce("rastreador.com", ["https://clk.rastreador.com", "https://sync.rastreador.com"]);
        Assert.Equal(["https://clk.rastreador.com", "https://sync.rastreador.com"], ledger.OriginsOf("rastreador.com"));
        // As origens chegam ao apagar do armazenamento ao lado das óbvias.
        var origins = SiteScope.Origins("rastreador.com", ledger.OriginsOf("rastreador.com"));
        Assert.Contains("https://clk.rastreador.com", origins);
        Assert.Contains("https://www.rastreador.com", origins);

        var again = BounceLedger.Load(ledger.Serialize(), () => now);
        Assert.Equal(["https://clk.rastreador.com", "https://sync.rastreador.com"], again.OriginsOf("rastreador.com"));
        // Um ficheiro antigo, sem origens, continua a carregar.
        Assert.Empty(BounceLedger.Load("{\"Bounces\":{\"a.com\":\"2026-11-07T10:00:00Z\"},\"Interactions\":{},\"Cleared\":0}", () => now).OriginsOf("a.com"));

        now += BounceLedger.Grace;
        ledger.MarkCleared("rastreador.com");
        Assert.Empty(ledger.OriginsOf("rastreador.com"));
        for (var i = 0; i < BounceLedger.MaxOriginsPerSite + 10; i++) ledger.RecordBounce("muitos.com", [$"https://s{i}.muitos.com"]);
        Assert.Equal(BounceLedger.MaxOriginsPerSite, ledger.OriginsOf("muitos.com").Count);
    }

    // Revisão aos 50: um SSO que começa num favorito, numa aba nova ou num link de outro site volta ao primeiro site da
    // cadeia, não ao de partida — o fornecedor de identidade não é um salto e não perde a sessão.
    [Theory]
    [InlineData("eteris://newtab/")]
    [InlineData(null)]
    [InlineData("https://webmail.pt/caixa")]
    public void SignInRoundTripToTheFirstSite_IsALoginNotABounce(string? initiator)
    {
        var chain = new RedirectChain(_ => false);
        chain.Begin(initiator, "https://outlook.office.com/mail/", T0);
        chain.Continue(null, "https://login.microsoftonline.com/common/oauth2/v2.0/authorize?client_id=1", T0.AddMilliseconds(300),
            serverRedirect: true, rewritten: false);
        chain.Continue(null, "https://outlook.office.com/mail/", T0.AddSeconds(1), serverRedirect: false, rewritten: false);
        Assert.Empty(chain.Close());
    }

    [Fact]
    public void RoundTrip_StillMarksAKnownTracker()
    {
        var chain = new RedirectChain(host => host.EndsWith("doubleclick.net", StringComparison.Ordinal));
        chain.Begin("https://jornal.pt/", "https://loja.pt/promo", T0);
        chain.Continue(null, "https://ad.doubleclick.net/ddm/clk", T0.AddMilliseconds(200), serverRedirect: true, rewritten: false);
        chain.Continue(null, "https://accounts.google.com/o/oauth2/auth", T0.AddMilliseconds(400), serverRedirect: true, rewritten: false);
        chain.Continue(null, "https://loja.pt/promo", T0.AddSeconds(1), serverRedirect: true, rewritten: false);
        Assert.Equal(["doubleclick.net"], Sites(chain.Close()));
    }

    // Revisão aos 50: o site é o da Public Suffix List — um salto por um alojamento partilhado marca só essa app, e
    // apagar os cookies dela não toca nas outras apps do mesmo alojamento.
    [Theory]
    [InlineData("https://links-x.azurewebsites.net/r?id=1", "links-x.azurewebsites.net", "minha-app.azurewebsites.net")]
    [InlineData("https://track.herokuapp.com/r", "track.herokuapp.com", "myapp.herokuapp.com")]
    [InlineData("https://go.exemplo.github.io/x", "exemplo.github.io", "outro.github.io")]
    public void SharedHosting_MarksOnlyTheAppThatBounced(string hop, string site, string neighbour)
    {
        var chain = new RedirectChain();
        chain.Begin("https://jornal.pt/", hop, T0);
        chain.Continue(null, "https://loja.pt/", T0.AddMilliseconds(200), serverRedirect: true, rewritten: false);
        Assert.Equal([site], Sites(chain.Close()));
        Assert.False(SiteScope.CookieBelongs("." + neighbour, site));
        Assert.True(SiteScope.CookieBelongs("." + site, site));
    }

    // Revisão aos 50: com «Esquecer os sites de salto» desligado nada fica registado (nem os sites onde se clicou), e
    // desligar esquece o que já se sabia; o Escudo continua a ver os saltos.
    [Fact]
    public void DisabledLedger_RecordsNothingAndForgetsWhatItKnew()
    {
        var ledger = new BounceLedger(() => T0);
        ledger.RecordBounce("rastreador.com", ["https://clk.rastreador.com"]);
        ledger.RecordInteraction("loja.pt");
        ledger.Enabled = false;
        Assert.Equal(0, ledger.PendingCount);
        Assert.Empty(ledger.OriginsOf("rastreador.com"));
        Assert.True(ledger.RecordBounce("loja.pt"));
        ledger.RecordInteraction("forum.pt");
        Assert.Equal(0, ledger.PendingCount);
        Assert.DoesNotContain("loja.pt", ledger.Serialize());
        Assert.DoesNotContain("forum.pt", ledger.Serialize());

        ledger.Enabled = true;
        Assert.True(ledger.RecordBounce("forum.pt"));
        Assert.Equal(1, ledger.PendingCount);

        var source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Eteris.Browser", "MainWindow.Bounce.cs"));
        Assert.Contains("ledger.Enabled = Data.Settings.Current.ForgetBounceTrackers;", source);
        Assert.Contains("Bounces.Enabled = settings.ForgetBounceTrackers;", source);
        Assert.Contains("File.Delete(BounceFile);", source);
        Assert.Contains("Bounces.OriginsOf(site)", source);
        Assert.Contains("AppData.History.SiteUrls(site)", source);
        Assert.Contains("new RedirectChain(TrackerBlocker.Default.Lists)", source);
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Eteris.slnx"))) dir = dir.Parent;
        return dir!.FullName;
    }
}
