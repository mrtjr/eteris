using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eteris.Browser.Sidebar;

// Um site fixado na barra lateral: abre dentro da barra, numa instância própria do motor, e persiste entre sessões.
// Mobile = «vista móvel»: os pedidos saem com o agente de um telemóvel (sites que servem uma versão estreita por agente).
// KeepAwake = «Manter acordado»: escondido na barra aberta, o site nunca adormece (um chat que tem de receber mensagens).
public sealed record WebPanel(string Url, string Title, bool Mobile = false, bool KeepAwake = false);

// Sites fixados: a lista (ordem, limite, duplicados) e a sua forma em ficheiro (paineis.json no perfil).
// Lógica pura; a janela cria e destrói os motores (MainWindow.WebPanels.cs).
public static class WebPanels
{
    public const int Max = 12;
    public const string Glyph = "";

    // Agente da vista móvel: um Android com o Chrome da mesma geração do motor (nada de identificar o Éteris).
    public const string MobileUserAgent =
        "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/156.0.0.0 Mobile Safari/537.36";

    // O mesmo telemóvel visto pelo JavaScript da página (navigator.userAgent, navigator.platform e
    // navigator.userAgentData) e pelas dicas de cliente: sem isto o servidor via um Android e o script um Windows,
    // os sites que decidem a disposição no cliente continuavam de secretária e a combinação era uma impressão
    // digital rara. Aplicado pelo DevTools (Emulation.setUserAgentOverride) no motor do site fixado.
    public const string MobilePlatform = "Android";
    public const string MobilePlatformVersion = "14.0.0";
    public const string MobileModel = "Pixel 8";
    public const string MobileNavigatorPlatform = "Linux armv8l";
    public const string MobileMajorVersion = "156";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    // Só endereços http(s), sem espaços à volta e sem credenciais embutidas (http://utilizador:senha@site/ ficaria
    // em claro em paineis.json e em settings.json — como no histórico, nunca são guardadas); o resto fica como o
    // utilizador o tinha (o site decide o caminho).
    public static string? Normalize(string? url)
    {
        var trimmed = url?.Trim();
        return SidebarPanels.IsWebUrl(trimmed) ? Data.HistoryStore.WithoutUserInfo(trimmed!) : null;
    }

    // O endereço traz utilizador/palavra-passe (que o site fixado não guarda).
    public static bool HasCredentials(string? url)
        => Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0;

    public static bool Contains(IReadOnlyList<WebPanel> panels, string? url)
        => Normalize(url) is { } clean && panels.Any(p => string.Equals(p.Url, clean, StringComparison.Ordinal));

    // Fixar: null quando o endereço não serve, já está fixado ou a barra está cheia.
    public static IReadOnlyList<WebPanel>? Add(IReadOnlyList<WebPanel> panels, string? url, string? title)
    {
        if (Normalize(url) is not { } clean || Contains(panels, clean) || panels.Count >= Max) return null;
        return [.. panels, new WebPanel(clean, CleanTitle(title))];
    }

    public static IReadOnlyList<WebPanel> Remove(IReadOnlyList<WebPanel> panels, string url)
        => panels.Where(p => !string.Equals(p.Url, url, StringComparison.Ordinal)).ToList();

    // Mover um site para cima (delta -1) ou para baixo (+1) na coluna; nas pontas fica como está.
    public static IReadOnlyList<WebPanel> Move(IReadOnlyList<WebPanel> panels, string url, int delta)
    {
        var index = IndexOf(panels, url);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= panels.Count || delta == 0) return panels;
        var list = panels.ToList();
        var item = list[index];
        list.RemoveAt(index);
        list.Insert(target, item);
        return list;
    }

    // Ligar ou desligar a vista móvel de um site; um site que não existe deixa a lista como está.
    public static IReadOnlyList<WebPanel> SetMobile(IReadOnlyList<WebPanel> panels, string url, bool mobile)
        => IndexOf(panels, url) < 0 ? panels : panels.Select(p => p.Url == url ? p with { Mobile = mobile } : p).ToList();

    // Ligar ou desligar o «Manter acordado» de um site; um site que não existe deixa a lista como está.
    public static IReadOnlyList<WebPanel> SetKeepAwake(IReadOnlyList<WebPanel> panels, string url, bool keepAwake)
        => IndexOf(panels, url) < 0 ? panels : panels.Select(p => p.Url == url ? p with { KeepAwake = keepAwake } : p).ToList();

    // Ajuste e6 (plano de desempenho da 0.2.14): um site fixado escondido (a barra aberta noutro separador) adormece como
    // as abas, ao fim do mesmo tempo (Definições › Abas; zero = nunca), salvo «Manter acordado». hiddenSince: quando
    // deixou de se ver (null: está à vista).
    public static bool ShouldSleep(bool keepAwake, DateTime? hiddenSinceUtc, DateTime nowUtc, TimeSpan threshold)
        => threshold > TimeSpan.Zero && !keepAwake && hiddenSinceUtc is { } since && nowUtc - since > threshold;

    public static WebPanel? Find(IReadOnlyList<WebPanel> panels, string url)
        => IndexOf(panels, url) is var index and >= 0 ? panels[index] : null;

    public static int IndexOf(IReadOnlyList<WebPanel> panels, string url)
    {
        for (var i = 0; i < panels.Count; i++)
            if (string.Equals(panels[i].Url, url, StringComparison.Ordinal)) return i;
        return -1;
    }

    // O separador da barra lateral que representa o site.
    public static SidebarPanel ToPanel(WebPanel panel) => new(SidebarPanels.WebKey(panel.Url), TitleOf(panel), Glyph, panel.Url);

    // Nome à vista: o título guardado, senão o site (sem «www.»).
    public static string TitleOf(WebPanel panel) => panel.Title.Length > 0 ? panel.Title : SidebarLists.HostOf(panel.Url);

    public static string CleanTitle(string? title)
    {
        var clean = string.Join(' ', (title ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length > 60 ? clean[..60].TrimEnd() + "…" : clean;
    }

    public static string Serialize(IReadOnlyList<WebPanel> panels) => JsonSerializer.Serialize(panels, Json);

    // Tolerante: ficheiro estragado ou com entradas inválidas dá a lista possível (nunca uma exceção).
    public static IReadOnlyList<WebPanel> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        List<WebPanel>? raw;
        try { raw = JsonSerializer.Deserialize<List<WebPanel>>(json, Json); }
        catch (JsonException) { return []; }
        var result = new List<WebPanel>();
        foreach (var panel in raw ?? [])
        {
            if (panel is null || Normalize(panel.Url) is not { } clean || result.Any(p => p.Url == clean) || result.Count >= Max) continue;
            result.Add(new WebPanel(clean, CleanTitle(panel.Title), panel.Mobile, panel.KeepAwake));
        }
        return result;
    }
}
