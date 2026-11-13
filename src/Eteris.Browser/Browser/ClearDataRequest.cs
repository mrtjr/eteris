namespace Eteris.Browser.Browser;

// «Apagar dados de navegação»: o histórico respeita o período; cookies, armazenamento dos sites e cache são apagados
// por completo. Vault: a caixa «Palavras-passe e preenchimento automático» (desmarcada por omissão): o cofre inteiro,
// com o Windows Hello.
public sealed record ClearDataRequest(bool History, bool Cookies, bool Cache, DateTime Since, bool Vault = false)
{
    public static ClearDataRequest? Parse(string what, string range, DateTime now)
    {
        var parts = what.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(p => p is not ("history" or "cookies" or "cache" or "vault"))) return null;
        if (HistoryPage.RangeStart(range, now) is not { } since) return null;
        return new ClearDataRequest(parts.Contains("history"), parts.Contains("cookies"), parts.Contains("cache"), since, parts.Contains("vault"));
    }

    public string Summary
    {
        get
        {
            var names = new List<string>();
            if (History) names.Add("histórico");
            if (Cookies) names.Add("cookies");
            if (Cache) names.Add("cache");
            if (names.Count == 0) return Vault ? "Palavras-passe e preenchimento automático apagados" : "Nada apagado";
            var text = names.Count == 1 ? names[0] : string.Join(", ", names[..^1]) + " e " + names[^1];
            return char.ToUpperInvariant(text[0]) + text[1..] + " apagados" + (Vault ? " · palavras-passe e preenchimento automático também" : string.Empty);
        }
    }

    // A resposta depois de o motor apagar: sem falhas, o resumo; com falhas, começa por «Não foi» (a página não mostra
    // o ✓) e diz o que ficou por apagar e o que saiu.
    public string Report(ClearOutcome outcome)
    {
        if (!outcome.Failed) return Summary;
        var failed = new List<string>();
        if (outcome.CookiesFailed) failed.Add("os cookies");
        if (outcome.CacheFailed) failed.Add("a cache");
        if (outcome.StorageFailed > 0) failed.Add(outcome.StorageFailed == 1 ? "os dados de 1 site" : $"os dados de {outcome.StorageFailed} sites");
        if (outcome.CookieSitesUnknown) failed.Add("o armazenamento dos sites que só tinham cookies");
        if (outcome.VideoEngineFailed) failed.Add("os dados do motor de vídeo");
        var cleared = new List<string>();
        if (History) cleared.Add("histórico");
        if (Cookies && !outcome.CookiesFailed) cleared.Add("cookies");
        if (Cache && !outcome.CacheFailed) cleared.Add("cache");
        if (Vault) cleared.Add("palavras-passe e preenchimento automático");
        var text = "Não foi possível apagar " + Join(failed) + ".";
        return cleared.Count == 0 ? text : text + " Apagado: " + Join(cleared) + ".";
    }

    // A resposta da rota: uma exceção a meio da limpeza responde «Não foi…» (sem ✓), nunca o texto genérico do handler.
    public static async Task<string> ReplyAsync(Func<Task<string>> clear)
    {
        try { return await clear().ConfigureAwait(false); }
        catch (Exception) { return "Não foi possível apagar os dados de navegação."; }
    }

    private static string Join(List<string> names)
        => names.Count == 1 ? names[0] : string.Join(", ", names[..^1]) + " e " + names[^1];
}

// O que o motor não conseguiu apagar num «Apagar dados»: StorageFailed conta as origens (sites) cujo armazenamento ficou.
// CookieSitesUnknown: o motor não deixou ler os cookies, e os sites que só se conheciam por eles ficaram com o armazenamento.
public sealed record ClearOutcome(bool CookiesFailed = false, bool CacheFailed = false, int StorageFailed = 0, bool VideoEngineFailed = false,
    bool CookieSitesUnknown = false)
{
    public bool Failed => CookiesFailed || CacheFailed || StorageFailed > 0 || VideoEngineFailed || CookieSitesUnknown;
}
