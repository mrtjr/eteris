using Eteris.Browser.Browser;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests;

// B7 + B8 à vista: a linha do Escudo dos rastreadores escondidos, as promessas em eteris://privacy/ e as Novidades.
public class HiddenTrackingTests
{
    private static PrivacyPage.State State(bool skip = true, bool forget = true, bool cname = true, string? dns = null,
        long decisions = 0, double micros = 0, CnameSource? source = null) => new(
        new Dictionary<string, string>(), PrivacySignals.Headers, GpcScript: true, ThirdPartyCookiesBlocked: true, WebRtcProtected: true,
        SecureDns: dns, Language: "pt-PT", HideFromCapture: false, VisibleWindows: 1, HiddenWindows: 0,
        SkipRedirects: skip, ForgetBounces: forget, BouncesCleared: 3, BouncesPending: 1,
        UncloakCname: cname, CnameBlocked: 2, CnameDecisions: decisions, CnameMicros: micros,
        CnameSource: source ?? (dns is null ? CnameSource.WindowsDns : CnameSource.SecureDns));

    private static PrivacyPage.Promise Find(PrivacyPage.State state, string id) => Assert.Single(PrivacyPage.Promises(state), p => p.Id == id);

    [Fact]
    public void ShieldLine_NamesTheAliasAndTheTracker()
    {
        Assert.Equal("✓  Rastreadores escondidos atrás de subdomínios do site são desmascarados", CnameUncloak.ShieldLine([], enabled: true));
        Assert.Equal("✓  1 rastreador escondido desmascarado: metrics.jornal.pt → jornal.eulerian.net",
            CnameUncloak.ShieldLine(["metrics.jornal.pt → jornal.eulerian.net"], enabled: true));
        Assert.Equal("✓  3 rastreadores escondidos desmascarados: a.jornal.pt → x.at-o.net e mais 1",
            CnameUncloak.ShieldLine(["a.jornal.pt → x.at-o.net", "b.jornal.pt → y.eulerian.net", "a.jornal.pt → x.at-o.net"], enabled: true));
        Assert.StartsWith("·", CnameUncloak.ShieldLine(["a.jornal.pt → x.at-o.net"], enabled: false));
    }

    [Fact]
    public void PrivacyPage_BouncePromise_IsDecidedByTheRealCode()
    {
        var bounce = Find(State(), "bounce");
        Assert.True(bounce.Kept);
        Assert.Contains("«www.exemplo.pt/artigo»", bounce.Detail);
        Assert.Contains("3 sites esquecidos, 1 à espera", bounce.Detail);
        Assert.Contains("Esquecer os sites de salto está desligado", Find(State(forget: false), "bounce").Detail);
        var off = Find(State(skip: false), "bounce");
        Assert.Null(off.Kept);
        Assert.Equal("eteris://settings/#privacidade", off.Fix);
    }

    [Fact]
    public void PrivacyPage_CnamePromise_ShowsTheSessionAndTheCost()
    {
        var cname = Find(State(decisions: 40, micros: 12.34), "cname");
        Assert.True(cname.Kept);
        Assert.Contains("metrics.exemplo.pt", cname.Detail);
        Assert.Contains("2 bloqueados em 40 pedidos vistos, 12.3 µs por pedido na thread de rede.", cname.Detail);
        // Revisão aos 50: com o DNS cifrado o nome canónico pergunta-se ao fornecedor pelo motor (a cache do Windows nunca
        // via esses nomes); com um proxy no Windows e sem DNS cifrado a promessa diz que está em pausa, não ✓.
        Assert.Contains("pergunta-se a Quad9 pelo próprio motor", Find(State(dns: "Quad9", decisions: 1, micros: 1), "cname").Detail);
        var paused = Find(State(source: CnameSource.Off), "cname");
        Assert.False(paused.Kept);
        Assert.StartsWith("Em pausa", paused.Detail);
        Assert.Equal("eteris://settings/#privacidade", paused.Fix);
        Assert.DoesNotContain("µs", Find(State(), "cname").Detail);
        var off = Find(State(cname: false), "cname");
        Assert.Null(off.Kept);
        Assert.Equal("eteris://settings/#privacidade", off.Fix);
    }

    [Fact]
    public void WhatsNew_HighlightsHiddenTracking()
    {
        var item = Assert.Single(WhatsNewPage.Highlights, h => h.Title == "Rastreio escondido");
        Assert.Contains("l.facebook.com", item.Detail);
        Assert.Contains("CNAME", item.Detail);
        // Entre os destaques da 0.2.9 (os 8 a seguir aos 2 da 0.2.11 e aos 5 da 0.2.10).
        Assert.Contains(item, WhatsNewPage.Highlights.Skip(7).Take(8));
    }

    [Fact]
    public void Settings_ShowsTheThreeToggles()
    {
        var html = SettingsPage.Render(new Data.UserSettings());
        Assert.Contains("data-key=\"debounce\"", html);
        Assert.Contains("data-key=\"bounceForget\"", html);
        Assert.Contains("data-key=\"cname\"", html);
    }
}
