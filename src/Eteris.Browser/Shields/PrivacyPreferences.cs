using CefSharp;

namespace Eteris.Browser.Shields;

// Preferências do Chromium aplicadas a um perfil (o global ou o de uma janela privada);
// os resultados do perfil global aparecem no painel do escudo.
public static class PrivacyPreferences
{
    // O Éteris tem histórico, favoritos e definições próprios: o Chromium não guarda nada disto por conta própria.
    public static readonly IReadOnlyDictionary<string, object> Hardening = new Dictionary<string, object>
    {
        ["history.saving_disabled"] = true,
        ["credentials_enable_service"] = false,
        ["credentials_enable_autosignin"] = false,
        ["autofill.profile_enabled"] = false,
        ["autofill.credit_card_enabled"] = false,
        ["net.network_prediction_options"] = 2,
        ["search.suggest_enabled"] = false,
        // As APIs de anúncios do Chromium também desligadas nas definições do perfil (além dos interruptores do motor,
        // Shields/EngineHardening.cs): nenhuma lista de interesses nem leilão fica ativo por omissão.
        ["privacy_sandbox.m1.topics_enabled"] = false,
        ["privacy_sandbox.m1.fledge_enabled"] = false,
        ["privacy_sandbox.m1.ad_measurement_enabled"] = false,
    };

    public static bool ThirdPartyCookiesBlocked { get; private set; }

    public static bool WebRtcIpProtected { get; private set; }

    // Fornecedor de DNS cifrado em vigor desde o arranque (null = DNS do sistema).
    public static string? StartedWithSecureDns { get; set; }

    public static string? SecureDnsProvider => StartedWithSecureDns;

    public static Task ApplyAsync(IRequestContext? context = null) => Cef.UIThreadTaskFactory.StartNew(() =>
    {
        var target = context ?? Cef.GetGlobalRequestContext();
        foreach (var (name, value) in Hardening) target.SetPreference(name, value, out _);
        var block = Data.Settings.Current.BlockThirdPartyCookies;
        var cookies = target.SetPreference("profile.block_third_party_cookies", block, out _)
                      | target.SetPreference("profile.cookie_controls_mode", block ? 1 : 0, out _);
        cookies &= block;
        var webRtc = target.SetPreference("webrtc.ip_handling_policy", "disable_non_proxied_udp", out _);
        if (context is not null) return;
        ThirdPartyCookiesBlocked = cookies;
        WebRtcIpProtected = webRtc;
    });
}
