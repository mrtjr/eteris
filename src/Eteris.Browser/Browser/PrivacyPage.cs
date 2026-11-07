using System.IO;
using System.Text.Json;
using Eteris.Browser.Shields;

namespace Eteris.Browser.Browser;

// eteris://privacy/ — o que o Éteris promete, cada promessa com ✔/✘ lido do estado em vigor (a linha de comandos que
// o motor recebeu, as preferências que o Chromium aceitou, os cabeçalhos que o tratador de pedidos põe, a afinidade das
// janelas no Windows) e, quando dá, confirmado ao vivo pelo JavaScript da própria página.
public static class PrivacyPage
{
    public const string Url = "eteris://privacy/";

    // Kept: true = cumprida, false = falhou, null = opcional e desligada (escolha do utilizador).
    public sealed record Promise(string Id, string Title, string Detail, bool? Kept, string? Live = null, string? Fix = null);

    public sealed record State(
        IReadOnlyDictionary<string, string> EngineArgs,
        IReadOnlyList<(string Name, string Value)> Headers,
        bool GpcScript,
        bool ThirdPartyCookiesBlocked,
        bool WebRtcProtected,
        string? SecureDns,
        bool HideFromCapture,
        int VisibleWindows,
        int HiddenWindows);

    private static readonly Lazy<string> Template = new(() =>
    {
        using var stream = typeof(PrivacyPage).Assembly.GetManifestResourceStream("Eteris.Browser.Browser.privacy.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static IReadOnlyList<Promise> Promises(State s)
    {
        var disabled = EngineHardening.Switch(s.EngineArgs, "disable-features");
        var hiddenFromJs = EngineHardening.Switch(s.EngineArgs, "disable-blink-features");
        var missingAds = EngineHardening.AdApis.Where(a => !disabled.Contains(a.Feature)).Select(a => a.Feature)
            .Concat(EngineHardening.AdBlinkFeatures.Where(f => !hiddenFromJs.Contains(f))).Distinct().ToList();
        var quiet = s.EngineArgs.ContainsKey("disable-background-networking") && s.EngineArgs.ContainsKey("disable-component-update")
                    && s.EngineArgs.TryGetValue("component-updater", out var updater) && updater.Contains(Infrastructure.CefConfigurator.NoUpdateServer, StringComparison.Ordinal);
        var gpcHeader = s.Headers.Contains(("Sec-GPC", "1")) && s.Headers.Contains(("DNT", "1"));

        return
        [
            new("gpc-header", "Pede a cada site para não o seguir",
                gpcHeader ? "Cada pedido leva os cabeçalhos «Sec-GPC: 1» (Global Privacy Control) e «DNT: 1»." : "Os pedidos saem sem o cabeçalho Sec-GPC.",
                gpcHeader),
            new("gpc-js", "O pedido é visível ao JavaScript dos sites",
                s.GpcScript ? "navigator.globalPrivacyControl vale true em todas as páginas — é aí que os avisos de cookies e o Consent Mode o leem."
                    : "O script do GPC não está registado.",
                s.GpcScript, Live: "gpc"),
            new("ad-apis", "Sem as APIs de anúncios do Chromium",
                missingAds.Count == 0
                    ? "Topics, Protected Audience (FLEDGE), Attribution Reporting, Shared Storage e Fenced Frames estão desligados no motor."
                    : "Ainda ligadas no motor: " + string.Join(", ", missingAds) + ".",
                missingAds.Count == 0, Live: "ads"),
            new("quiet", "Nada sai do computador sem um pedido seu",
                quiet ? "Sem atualizações automáticas de componentes da Google nem tráfego de fundo do Chromium."
                    : "O motor arrancou sem os interruptores que cortam o tráfego de fundo.",
                quiet),
            new("cookies", "Cookies de terceiros bloqueados",
                s.ThirdPartyCookiesBlocked ? "Os anunciantes embutidos nas páginas não guardam nem leem cookies." : "Desligado nas Definições: os terceiros podem guardar cookies.",
                s.ThirdPartyCookiesBlocked, Fix: s.ThirdPartyCookiesBlocked ? null : "eteris://settings/#privacidade"),
            new("webrtc", "O WebRTC não revela o seu IP local",
                s.WebRtcProtected ? "As chamadas de vídeo funcionam, mas os sites não descobrem os endereços da sua rede." : "A política de IP do WebRTC não foi aplicada.",
                s.WebRtcProtected),
            new("dns", "DNS cifrado",
                s.SecureDns is { } dns ? $"Os nomes dos sites vão cifrados para {dns}." : "Opcional · desligado: os nomes dos sites vão para o DNS do sistema.",
                s.SecureDns is null ? null : true, Fix: s.SecureDns is null ? "eteris://settings/#privacidade" : null),
            new("recall", "Escondido do Recall e das capturas de ecrã",
                !s.HideFromCapture ? "Opcional · desligado: com ele ligado as capturas, gravações e partilhas de ecrã mostram um vazio."
                    : s.HiddenWindows >= s.VisibleWindows && s.VisibleWindows > 0 ? $"As {s.VisibleWindows} janela(s) do Éteris ficam fora do Recall e das capturas."
                    : $"Só {s.HiddenWindows} de {s.VisibleWindows} janela(s) aceitaram ficar escondidas.",
                !s.HideFromCapture ? null : s.HiddenWindows >= s.VisibleWindows && s.VisibleWindows > 0,
                Fix: "eteris://settings/#privacidade"),
        ];
    }

    public static State Current()
    {
        var (visible, hidden) = Infrastructure.CaptureShield.Count();
        return new(EngineHardening.Applied, PrivacySignals.Headers, PrivacySignals.ScriptFor("https://example.com/") is not null,
            PrivacyPreferences.ThirdPartyCookiesBlocked, PrivacyPreferences.WebRtcIpProtected, PrivacyPreferences.SecureDnsProvider,
            Data.Settings.Current.HideFromCapture, visible, hidden);
    }

    public static string Render(State state)
    {
        var promises = Promises(state);
        var data = new
        {
            promises = promises.Select(p => new { id = p.Id, title = p.Title, detail = p.Detail, kept = p.Kept, live = p.Live, fix = p.Fix }),
        };
        return Template.Value.Replace("{{DATA}}", JsonSerializer.Serialize(data));
    }
}
