namespace Eteris.Browser.Shields;

// DNS-over-HTTPS: as consultas de nomes vão cifradas para um fornecedor escolhido em vez de irem em claro.
// Modo «secure» (sem recurso ao DNS do sistema), como a opção «Usar DNS seguro» do Chrome.
public static class SecureDns
{
    public static readonly IReadOnlyDictionary<string, (string Name, string Template)> Providers = new Dictionary<string, (string, string)>
    {
        ["cloudflare"] = ("Cloudflare", "https://chrome.cloudflare-dns.com/dns-query"),
        ["quad9"] = ("Quad9", "https://dns.quad9.net/dns-query"),
        ["mullvad"] = ("Mullvad", "https://dns.mullvad.net/dns-query"),
    };

    public static string? TemplateFor(string id) => Providers.TryGetValue(id, out var p) ? p.Template : null;

    public static string ModeFor(string id) => TemplateFor(id) is null ? "off" : "secure";
}
