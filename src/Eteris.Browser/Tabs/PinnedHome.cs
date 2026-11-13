namespace Eteris.Browser.Tabs;

// A «casa» de uma aba fixada (BrowserTab.Home), sem WPF: o endereço em que foi fixada, para onde «Voltar a casa» (clique
// no ícone da aba aberta, Alt+Home, menu da aba, paleta) a leva. Fica só na sessão deste computador (Data/SessionStore.cs,
// SessionTabMeta.Home): não há sincronização de abas e um endereço pode trazer tokens. Só lê o endereço: nunca há pedidos.
//   - CanBeHome: só endereços web (http/https com anfitrião). A nova aba e as páginas do Éteris não são casa.
//   - Clean: o endereço guardado, sem «utilizador:senha@» (nunca vai para o session.json), com o resto como estava
//     (#fragmento incluído, para voltar exatamente onde se fixou).
//   - Key/Same: «a mesma casa». Esquema e anfitrião sem maiúsculas, porta só quando não é a omissão, o caminho como está
//     (maiúsculas incluídas) sem as barras finais, a query sem os parâmetros de rastreio (Shields/UrlCleaner.cs; a ordem
//     conta e um «?» vazio é como não ter), sem fragmento nem utilizador. http ≠ https e www. ≠ sem www.: quem os casa é
//     o redirecionamento ao voltar (IsRedirectOfHome), e só esses dois.
public static class PinnedHome
{
    public const int MaxLabelLength = 40;

    public static bool CanBeHome(string? address)
    {
        var trimmed = address?.Trim();
        return Sidebar.SidebarPanels.IsWebUrl(trimmed) && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Host.Length > 0;
    }

    public static string? Clean(string? address) => CanBeHome(address) ? Data.HistoryStore.WithoutUserInfo(address!.Trim()) : null;

    public static string? Key(string? address) => Parts(address) is { } p ? $"{p.Scheme}://{p.Host}{p.Port}{p.Rest}" : null;

    public static bool Same(string? home, string? address) => Key(home) is { } key && string.Equals(key, Key(address), StringComparison.Ordinal);

    // Uma fixada sem casa (fixada na nova aba ou numa página do Éteris, ou de uma sessão antiga numa delas) fica com a casa na
    // 1.ª página web que abrir; nos outros casos a casa não muda.
    public static string? Adopt(bool pinned, string? home, string newAddress)
        => pinned && home is null && CanBeHome(newAddress) ? Clean(newAddress) : home;

    // Ao voltar a casa, o endereço a que se chegou é a mesma casa tirando duas diferenças que o site (ou o «HTTPS primeiro»)
    // impõe: o esquema subir de http para https e um «www.» à frente do anfitrião (a mais ou a menos). O caminho, a query e
    // a porta têm de ser iguais: uma página de entrada no mesmo site (/login?next=…) nunca passa a ser a casa.
    public static bool IsRedirectOfHome(string home, string landed)
    {
        if (Parts(home) is not { } h || Parts(landed) is not { } l) return false;
        var scheme = h.Scheme == l.Scheme || (h.Scheme == Uri.UriSchemeHttp && l.Scheme == Uri.UriSchemeHttps);
        var host = h.Host == l.Host || h.Host == "www." + l.Host || "www." + h.Host == l.Host;
        return scheme && host && h.Port == l.Port && string.Equals(h.Rest, l.Rest, StringComparison.Ordinal);
    }

    // O anfitrião sem «www.» (com a porta, se não for a omissão) e o caminho quando não é a raiz, sem query nem fragmento;
    // no máximo MaxLabelLength caracteres, cortado com «…».
    public static string Label(string home)
    {
        if (!Uri.TryCreate(home.Trim(), UriKind.Absolute, out var uri) || uri.Host.Length == 0) return home;
        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        if (!uri.IsDefaultPort) host += $":{uri.Port}";
        var path = Readable(uri.AbsolutePath.TrimEnd('/'));
        var label = host.ToLowerInvariant() + path;
        return label.Length <= MaxLabelLength ? label : label[..(MaxLabelLength - 1)] + "…";
    }

    // A leitura do ficheiro para uma aba fixada: a casa gravada, quando serve; senão o endereço da aba (sessão antiga, ou um
    // valor estragado ou editado à mão); senão nenhuma (a aba adota a 1.ª página web).
    public static string? Restore(string? saved, string address) => Clean(saved) ?? Clean(address);

    private sealed record HomeParts(string Scheme, string Host, string Port, string Rest);

    private static HomeParts? Parts(string? address)
    {
        if (!CanBeHome(address) || !Uri.TryCreate(address!.Trim(), UriKind.Absolute, out var uri)) return null;
        var scheme = uri.Scheme.ToLowerInvariant();
        var host = uri.IdnHost.ToLowerInvariant();
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        var path = uri.AbsolutePath.TrimEnd('/');
        // Só a query passa pelo limpador (que precisa de um endereço http inteiro); o caminho fica fora dele.
        var cleaned = Shields.UrlCleaner.Clean($"{scheme}://h/{uri.Query}");
        var query = cleaned.IndexOf('?') is var q and >= 0 ? cleaned[q..].TrimEnd('?', '&') : string.Empty;
        return new HomeParts(scheme, host, port, path + query);
    }

    // Os caracteres escapados do caminho ficam legíveis (/wiki/S%C3%A3o_Paulo → /wiki/São_Paulo), sem caracteres de controlo
    // nem de formatação (os de direção do texto baralhavam o rótulo).
    private static string Readable(string path)
    {
        string text;
        try { text = Uri.UnescapeDataString(path); }
        catch (UriFormatException) { text = path; }
        return new string(text.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray());
    }
}
