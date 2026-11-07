using System.Globalization;

namespace Eteris.Browser.Shields;

// O que cada pedido diz sobre o computador: uma só língua e as dicas do cliente (client hints) mínimas.
public static class RequestIdentity
{
    public static readonly string[] LanguageChoices = ["pt-PT", "system", "en-US"];

    // «Língua enviada aos sites» (Definições › Privacidade): uma só língua, sem a lista com pesos que distingue quem
    // a envia. «system» é a língua do Windows (pt-BR, por exemplo); sem região conhecida, pt-PT.
    public static string Language(string choice, CultureInfo? system = null)
    {
        if (choice != "system") return LanguageChoices.Contains(choice) ? choice : "pt-PT";
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

    // As que um site pode pedir (Accept-CH / Critical-CH) e que separam este computador dos outros: versão completa,
    // arquitetura, bits, modelo, versão do Windows, memória, ecrã, rede. Nunca saem.
    private static readonly HashSet<string> LegacyHints = new(StringComparer.OrdinalIgnoreCase)
        { "device-memory", "dpr", "width", "viewport-width", "rtt", "downlink", "ect" };

    public static bool IsIdentifyingHint(string header)
        => LegacyHints.Contains(header)
           || header.StartsWith("sec-ch-", StringComparison.OrdinalIgnoreCase)
              && !LowEntropyHints.Contains(header.ToLowerInvariant());
}
