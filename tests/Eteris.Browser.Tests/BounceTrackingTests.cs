using CefSharp;
using Eteris.Browser.Data;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

// B7 (parte 2) «Esquecer os sites de salto»: cadeias de navegação e prazo com relógio injetado.
public class BounceTrackingTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ServerBounceBetweenTwoSites_MarksTheMiddleSite()
    {
        var chain = new RedirectChain();
        chain.Begin("https://jornal.pt/artigo", "https://t.rastreador.com/c?id=1", T0);
        chain.Continue(null, "https://loja.pt/promo", T0.AddMilliseconds(200), serverRedirect: true, rewritten: false);
        Assert.Equal(["rastreador.com"], chain.Close());
    }

    [Fact]
    public void ClientBounceWithoutGesture_IsAlsoABounce()
    {
        var chain = new RedirectChain();
        chain.Begin("https://jornal.pt/", "https://clk.rastreador.com/x", T0);
        chain.Continue("https://clk.rastreador.com/x", "https://sync.outro-rastreador.net/s", T0.AddSeconds(1), serverRedirect: false, rewritten: false);
        chain.Continue("https://sync.outro-rastreador.net/s", "https://loja.pt/", T0.AddSeconds(2), serverRedirect: false, rewritten: false);
        Assert.Equal(["rastreador.com", "outro-rastreador.net"], chain.Close());
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
        Assert.Equal(["rastreador.com"], chain.Begin("https://b.pt/", "https://c.pt/", T0.AddMinutes(1)));
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
}
