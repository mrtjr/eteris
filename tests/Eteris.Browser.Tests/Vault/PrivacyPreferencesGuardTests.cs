using Eteris.Browser.Browser;
using Eteris.Browser.Infrastructure;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Tests.Vault;

// A0 (docs/palavras-passe.md): o gestor e o preenchimento do Chromium continuam desligados e nada dos formulários vai
// para a Google. Medido ao vivo: com as quatro preferências a false, um formulário de início de sessão enviado num
// perfil novo ainda levava o par utilizador/palavra-passe (com hash) a passwordsleakcheck-pa.googleapis.com
// (/v1/leaks:lookupSingle) — a verificação de fugas do Chromium corre mesmo sem oferecer para guardar.
public class PrivacyPreferencesGuardTests
{
    [Theory]
    [InlineData("credentials_enable_service")]
    [InlineData("credentials_enable_autosignin")]
    [InlineData("autofill.profile_enabled")]
    [InlineData("autofill.credit_card_enabled")]
    public void ChromiumPasswordManagerAndAutofill_StayOff(string preference)
        => Assert.Equal(false, PrivacyPreferences.Hardening[preference]);

    [Fact]
    public void ChromiumLeakCheck_IsOff()
    {
        Assert.Equal("profile.password_manager_leak_detection", PrivacyPreferences.LeakCheckPreference);
        Assert.Equal(false, PrivacyPreferences.Hardening[PrivacyPreferences.LeakCheckPreference]);
    }

    // Desligada é o valor em vigor no perfil, lido de volta (como o WebRTC), não o êxito do SetPreference.
    [Fact]
    public void LeakCheckOff_IsTheValueInTheProfile()
    {
        Assert.True(PrivacyPreferences.IsLeakCheckOff(false));
        Assert.False(PrivacyPreferences.IsLeakCheckOff(true));
        Assert.False(PrivacyPreferences.IsLeakCheckOff(null));
    }

    // O servidor de previsões de formulários (content-autofill.googleapis.com): 0 pedidos medidos, mas fica cortado no
    // motor desde o primeiro processo para não depender de o preenchimento do Chromium estar desligado.
    [Fact]
    public void Build_SendsNoFormsToGoogle()
    {
        var disabled = EngineHardening.Switch(CefConfigurator.Build("p").CefCommandLineArgs, "disable-features");
        Assert.Contains("AutofillServerCommunication", disabled);
        Assert.Contains(EngineHardening.NoGoogleOnPages, f => f.Feature == "AutofillServerCommunication" && !string.IsNullOrWhiteSpace(f.What));
    }

    private static PrivacyPage.State Good(Dictionary<string, string>? args = null) => new(
        args ?? new(CefConfigurator.Build("p").CefCommandLineArgs), PrivacySignals.Headers, GpcScript: true, ThirdPartyCookiesBlocked: true,
        WebRtcProtected: true, SecureDns: null, Language: "pt-PT", HideFromCapture: false, VisibleWindows: 1, HiddenWindows: 0);

    // «Nada sai do computador sem um pedido seu» passa a dizer também os formulários, lidos do estado real.
    [Fact]
    public void QuietPromise_CoversFormsSentToSites()
    {
        var kept = Assert.Single(PrivacyPage.Promises(Good()), p => p.Id == "quiet");
        Assert.True(kept.Kept);
        Assert.Contains("passwordsleakcheck-pa.googleapis.com", kept.Detail);
        Assert.Contains("content-autofill.googleapis.com", kept.Detail);

        var leak = Assert.Single(PrivacyPage.Promises(Good() with { LeakCheckOff = false }), p => p.Id == "quiet");
        Assert.False(leak.Kept);
        Assert.Contains("verificação de fugas", leak.Detail);

        var args = new Dictionary<string, string>(CefConfigurator.Build("p").CefCommandLineArgs);
        args["disable-features"] = string.Join(',', EngineHardening.Switch(args, "disable-features").Where(f => f != "AutofillServerCommunication"));
        var forms = Assert.Single(PrivacyPage.Promises(Good(args)), p => p.Id == "quiet");
        Assert.False(forms.Kept);
        Assert.Contains("AutofillServerCommunication", forms.Detail);
    }
}
