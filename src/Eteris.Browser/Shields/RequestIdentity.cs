using System.Globalization;

namespace Eteris.Browser.Shields;

// O que cada pedido diz sobre o computador: uma só língua e as dicas do cliente (client hints) mínimas.
public static class RequestIdentity
{
    public static readonly string[] LanguageChoices = ["pt-PT", "system", "en-US"];

    // «Língua enviada aos sites» (Definições › Privacidade): uma só língua, sem a lista com pesos que distingue quem
    // a envia. «system» (a predefinida) é a língua do Windows (pt-BR, por exemplo); sem região conhecida, pt-PT. Um
    // valor desconhecido vale a predefinida.
    public static string Language(string choice, CultureInfo? system = null)
    {
        if (choice != "system" && LanguageChoices.Contains(choice)) return choice;
        var name = (system ?? CultureInfo.CurrentUICulture).Name;
        if (!name.Contains('-') && name.Length > 0)
        {
            // Só a língua («pt»): a região mais comum dela (pt-BR), como o Windows faria.
            try { name = CultureInfo.CreateSpecificCulture(name).Name; }
            catch (CultureNotFoundException) { name = string.Empty; }
        }
        return name.Contains('-') ? name : "pt-PT";
    }

    // Dicas de baixa entropia que o Chromium envia sempre (marca, «é móvel?», plataforma): ficam.
    public static readonly string[] LowEntropyHints = ["sec-ch-ua", "sec-ch-ua-mobile", "sec-ch-ua-platform"];

    // Preferências do utilizador que qualquer página já lê pelo CSS (prefers-color-scheme, prefers-reduced-motion…):
    // cortá-las nos cabeçalhos não esconde nada e só fazia o site servir o tema claro a quem usa o escuro.
    public static readonly string[] PreferenceHints =
        ["sec-ch-prefers-color-scheme", "sec-ch-prefers-reduced-motion", "sec-ch-prefers-reduced-transparency", "sec-ch-prefers-contrast"];

    // As que um site pode pedir (Accept-CH / Critical-CH) e que separam este computador dos outros: versão completa,
    // arquitetura, bits, modelo, versão do Windows, memória, ecrã, rede. Nunca saem.
    private static readonly HashSet<string> LegacyHints = new(StringComparer.OrdinalIgnoreCase)
        { "device-memory", "dpr", "width", "viewport-width", "rtt", "downlink", "ect" };

    public static bool IsIdentifyingHint(string header)
        => LegacyHints.Contains(header)
           || header.StartsWith("sec-ch-", StringComparison.OrdinalIgnoreCase)
              && !LowEntropyHints.Contains(header.ToLowerInvariant()) && !PreferenceHints.Contains(header.ToLowerInvariant());

    // O Accept-Language de um pedido: as navegações levam sempre a língua com que o motor arrancou; nos sub-recursos o
    // Chromium não o põe, por isso um valor já presente foi a página que o escolheu (fetch com {headers}) e fica — salvo
    // a lista com pesos que o próprio motor gera a partir da língua («pt-PT,pt;q=0.9»). Null: deixar como está.
    public static string? AcceptLanguage(string? current, string started, bool navigation)
    {
        if (started.Length == 0) return null;
        if (navigation || string.IsNullOrEmpty(current)) return current == started ? null : started;
        return current.StartsWith(started + ",", StringComparison.OrdinalIgnoreCase) ? started : null;
    }
}
