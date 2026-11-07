namespace Eteris.Browser.Browser;

// O que um site sabe de si, para o cartão do cadeado: os factos são lidos na hora (cookies do contexto da janela,
// armazenamento pelo DevTools, Observatório e registo da rede local da aba) e passados em palavras aqui.
public static class SiteKnowledge
{
    // Cookie: null em Expires = só até fechar o Éteris (cookie de sessão). StorageBytes: bases de dados, cache,
    // trabalhadores e localStorage de todas as origens conhecidas do site. ShieldOff: o Escudo está desligado no site
    // (o limite do referrer não se aplica).
    public sealed record Facts(
        IReadOnlyList<DateTime?> CookieExpiries,
        long? StorageBytes,
        int ThirdParties,
        int Companies,
        int ThirdPartiesBlocked,
        string? ReferrerToOthers,
        int LocalNetworkAttempts,
        DateTime Now,
        bool ShieldOff = false);

    public sealed record Line(string Glyph, string Title, string Detail);

    public static IReadOnlyList<Line> Describe(Facts f)
    {
        var lines = new List<Line> { Cookies(f.CookieExpiries, f.Now) };
        lines.Add(f.StorageBytes switch
        {
            null => new("", "Armazenamento: por medir", "O Chromium não respondeu a tempo."),
            0 => new("", "Nada guardado no armazenamento", "Sem armazenamento local, IndexedDB nem cache de scripts deste site."),
            var bytes => new("", $"≈ {Size(bytes.Value)} guardados", "Armazenamento local, IndexedDB e cache de scripts e trabalhadores deste site."),
        });
        lines.Add(f.ThirdParties == 0
            ? new Line("", "Nenhum terceiro contactado", "Esta página só falou com o próprio site.")
            : new Line("", f.ThirdParties == 1 ? "Falou com 1 domínio de outro site" : $"Falou com {f.ThirdParties} domínios de outros sites",
                (f.Companies switch { 0 => "Nenhuma empresa conhecida", 1 => "De 1 empresa conhecida", _ => $"De {f.Companies} empresas conhecidas" })
                + (f.ThirdPartiesBlocked == 0 ? " · nenhum bloqueado" : f.ThirdPartiesBlocked == 1 ? " · 1 bloqueado" : $" · {f.ThirdPartiesBlocked} bloqueados")
                + " — veja-os no Observatório."));
        lines.Add(f.ShieldOff
            ? new Line("", "Referrer: o que a página decidir", "Com o Escudo desligado neste site, o Éteris não o limita: pode seguir o endereço completo desta página.")
            : f.ReferrerToOthers is { } referrer
            ? new Line("", "Referrer enviado: só a origem", $"Os outros sites recebem «{referrer}» como origem, nunca o endereço completo desta página.")
            : new Line("", "Referrer enviado: nada", "Desta página para outros sites não segue referrer."));
        lines.Add(f.LocalNetworkAttempts == 0
            ? new Line("", "Não tentou falar com a sua rede local", "Nenhum pedido a este computador, ao router ou a outros aparelhos da rede.")
            : new Line("", f.LocalNetworkAttempts == 1 ? "Tentou 1 vez falar com a sua rede local" : $"Tentou {f.LocalNetworkAttempts} vezes falar com a sua rede local",
                "Travado: uma página da Internet não chega a este computador nem à rede de casa sem a sua autorização."));
        return lines;
    }

    private static Line Cookies(IReadOnlyList<DateTime?> expiries, DateTime now)
    {
        if (expiries.Count == 0) return new("", "Nenhum cookie guardado", "Este site não deixou cookies neste computador.");
        var title = expiries.Count == 1 ? "1 cookie guardado" : $"{expiries.Count} cookies guardados";
        var session = expiries.Count(e => e is null);
        var longest = expiries.Where(e => e is not null).Select(e => e!.Value - now).DefaultIfEmpty(TimeSpan.Zero).Max();
        var detail = longest > TimeSpan.Zero
            ? $"O mais duradouro fica {Duration(longest)}"
              + (session == 0 ? "." : session == 1 ? "; 1 só até fechar o Éteris." : $"; {session} só até fechar o Éteris.")
            : session == 1 ? "Só até fechar o Éteris." : "Todos só até fechar o Éteris.";
        return new("", title, detail);
    }

    // «1 ano e 1 mês», «3 meses», «12 dias», «5 horas», «menos de 1 hora».
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.FromHours(1)) return "menos de 1 hora";
        if (span < TimeSpan.FromDays(1)) return Plural((int)span.TotalHours, "hora", "horas");
        var days = (int)span.TotalDays;
        if (days < 31) return Plural(days, "dia", "dias");
        var months = (int)(days / 30.44);
        if (months < 12) return Plural(months, "mês", "meses");
        var years = months / 12;
        var rest = months % 12;
        return Plural(years, "ano", "anos") + (rest == 0 ? string.Empty : " e " + Plural(rest, "mês", "meses"));
    }

    // «1,2 MB», «340 KB», «12 bytes».
    public static string Size(long bytes)
    {
        var pt = System.Globalization.CultureInfo.GetCultureInfo("pt-PT");
        if (bytes < 1024) return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0", pt) + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("0.0", pt) + " MB";
        return (bytes / 1024.0 / 1024 / 1024).ToString("0.0", pt) + " GB";
    }

    private static string Plural(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n} {many}";
}
