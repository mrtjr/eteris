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
        // Sem «conjuntos de sites relacionados»: nenhum grupo de domínios partilha cookies como se fosse um só site.
        ["privacy_sandbox.first_party_sets_enabled"] = false,
    };

    public static bool ThirdPartyCookiesBlocked { get; private set; }

    public static bool WebRtcIpProtected { get; private set; }

    public const string WebRtcPreference = "webrtc.ip_handling_policy";

    // Protegido é o valor em vigor no perfil, não o êxito do SetPreference: com a política fixada na linha de comandos
    // do motor (EngineHardening.WebRtcPolicySwitch) o Chromium recusa mudá-la — e é aí que está mais segura.
    public static bool IsWebRtcProtected(object? value) => value is string policy && policy == EngineHardening.WebRtcPolicy;

    // A língua que os sites recebem, já resolvida («system» → a do Windows).
    public static string SiteLanguage => RequestIdentity.Language(Data.Settings.Current.SiteLanguage);

    // Fornecedor de DNS cifrado em vigor desde o arranque (null = DNS do sistema).
    public static string? StartedWithSecureDns { get; set; }

    public static string? SecureDnsProvider => StartedWithSecureDns;

    // O modelo DoH desse fornecedor (https://…/dns-query), para o motor perguntar ele próprio (Shields/CnameUncloak.cs).
    public static string? SecureDnsTemplate { get; set; }

    public static Task ApplyAsync(IRequestContext? context = null) => Cef.UIThreadTaskFactory.StartNew(() =>
    {
        var target = context ?? Cef.GetGlobalRequestContext();
        foreach (var (name, value) in Hardening) target.SetPreference(name, value, out _);
        var block = Data.Settings.Current.BlockThirdPartyCookies;
        var cookies = target.SetPreference("profile.block_third_party_cookies", block, out _)
                      | target.SetPreference("profile.cookie_controls_mode", block ? 1 : 0, out _);
        // O interruptor «bloquear todos os cookies de terceiros» das versões novas do Chromium, a par do antigo.
        target.SetPreference("tracking_protection.block_all_3pc_toggle_enabled", block, out _);
        cookies &= block;
        target.SetPreference(WebRtcPreference, EngineHardening.WebRtcPolicy, out _);
        var webRtc = IsWebRtcProtected(target.GetPreference(WebRtcPreference));
        if (context is not null) return;
        ThirdPartyCookiesBlocked = cookies;
        WebRtcIpProtected = webRtc;
    });
}
