using CefSharp;

namespace Eteris.Browser.Shields;

// Preferências do Chromium aplicadas a um perfil (o global ou o de uma janela privada);
// os resultados do perfil global aparecem no painel do escudo.
public static class PrivacyPreferences
{
    public static bool ThirdPartyCookiesBlocked { get; private set; }

    public static bool WebRtcIpProtected { get; private set; }

    public static Task ApplyAsync(IRequestContext? context = null) => Cef.UIThreadTaskFactory.StartNew(() =>
    {
        var target = context ?? Cef.GetGlobalRequestContext();
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
