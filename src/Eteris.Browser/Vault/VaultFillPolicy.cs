namespace Eteris.Browser.Vault;

// Uma linha da lista de sugestões: o utilizador e de onde é (nunca a palavra-passe).
public sealed record SuggestionRow(string Id, string Title, string Detail, bool Insecure, OriginMatch Match)
{
    public override string ToString() => $"SuggestionRow {Id}";
}

public enum FillVerdict { Allowed, TabHidden, WindowInactive, DocumentGone, OtherSite }

// O que se sugere e quando se pode preencher (docs/palavras-passe.md, C3; desenho, secções 3.6 e 6). Decisões puras.
public static class VaultFillPolicy
{
    // Contra cliques roubados (clickjacking): um clique na lista logo depois de ela aparecer não conta.
    public static readonly TimeSpan ClickGuard = TimeSpan.FromMilliseconds(300);

    public const string NoUsername = "(sem utilizador)";
    public const string InsecureDetail = "Ligação não segura";

    // As palavras-passe para a página: só a mesma origem e o mesmo domínio registável (confirmado aqui outra vez), as
    // exatas primeiro, as outras marcadas «de conta.exemplo.com». Uma de https nunca aparece em http; uma de http numa
    // página http (fora do próprio computador) leva o aviso. prefix: o que o utilizador já escreveu no campo do utilizador.
    public static IReadOnlyList<SuggestionRow> Suggestions(IEnumerable<LoginEntry> logins, string pageOrigin, string? prefix)
    {
        var insecure = !Origins.IsSecure(pageOrigin);
        return logins
            .Select(login => (login, match: Origins.Match(login.Origin, pageOrigin)))
            .Where(x => x.match != OriginMatch.None)
            .Where(x => string.IsNullOrEmpty(prefix) || x.login.Username.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(x => x.match == OriginMatch.Exact ? 0 : 1)
            .Select(x => new SuggestionRow(
                x.login.Id,
                string.IsNullOrEmpty(x.login.Username) ? NoUsername : x.login.Username,
                insecure ? InsecureDetail
                    : x.match == OriginMatch.Exact ? Origins.DisplayHost(x.login.Origin) ?? x.login.Origin
                    : "de " + (Origins.DisplayHost(x.login.Origin) ?? x.login.Origin),
                insecure,
                x.match))
            .ToList();
    }

    // Onde aparecem as palavras-passe guardadas: no campo do utilizador e na palavra-passe atual de um início de sessão
    // ou de uma mudança (a nova é do gerador, C5).
    public static bool OffersLogins(string? kind, string? formKind)
        => kind == "username" || (kind == "password" && formKind is "login" or "change");

    public sealed record FillCheck(bool TabVisible, bool WindowActive, bool SameDocument, string SavedOrigin, string PageOrigin);

    // Preencher só na aba à vista da janela ativa, no mesmo documento que pediu, e com uma credencial deste site.
    public static FillVerdict CanFill(FillCheck check)
    {
        if (!check.SameDocument) return FillVerdict.DocumentGone;
        if (!check.TabVisible) return FillVerdict.TabHidden;
        if (!check.WindowActive) return FillVerdict.WindowInactive;
        return Origins.Match(check.SavedOrigin, check.PageOrigin) == OriginMatch.None ? FillVerdict.OtherSite : FillVerdict.Allowed;
    }

    // A pergunta quando o utilizador escolhe à mão uma palavra-passe de outro site (menu de contexto, C6).
    public static string OtherSiteQuestion(string savedOrigin, string pageOrigin)
        => $"Esta palavra-passe é de {Origins.DisplayHost(savedOrigin) ?? savedOrigin}. Usar em {Origins.DisplayHost(pageOrigin) ?? pageOrigin}?";

    public static bool AcceptsClick(TimeSpan sinceShown) => sinceShown >= ClickGuard;

    // D-5: «Pedir o Windows Hello ao preencher palavras-passe» (deste PC, desligado por omissão).
    public static bool NeedsReauth(bool askHello) => ReauthPolicy.Needs(ReauthPurpose.FillPassword, askHello, askPayments: true);
}
