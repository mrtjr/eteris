namespace Eteris.Browser.Vault;

// Uma linha da lista de sugestões: o utilizador e de onde é (nunca a palavra-passe).
public sealed record SuggestionRow(string Id, string Title, string Detail, bool Insecure, OriginMatch Match)
{
    // Um ícone (gerador, contacto, cartão…) no lugar da inicial do utilizador.
    public string? Glyph { get; init; }

    public override string ToString() => $"SuggestionRow {Id}";
}

public enum FillVerdict { Allowed, TabHidden, WindowInactive, DocumentGone, OtherSite }

// O que se sugere e quando se pode preencher (docs/palavras-passe.md, C3; desenho, secções 3.6 e 6). Decisões puras.
public static class VaultFillPolicy
{
    // Contra cliques roubados (clickjacking): um clique na lista logo depois de ela aparecer não conta.
    public static readonly TimeSpan ClickGuard = TimeSpan.FromMilliseconds(500);

    // Em píxeis do ecrã: a mão treme ao clicar; uma lista que sai de baixo de um cursor parado dá 0.
    public const double PointerSlack = 4.0;

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

    // A pergunta quando o utilizador escolhe à mão uma palavra-passe de outro site (menu de contexto, C6): as duas origens
    // completas (esquema, anfitrião como na barra de endereço e porta) e, numa página http, o aviso.
    public static string OtherSiteQuestion(string savedOrigin, string pageOrigin)
    {
        var question = $"Esta palavra-passe é de {FullOrigin(savedOrigin)}. Usar em {FullOrigin(pageOrigin)}?";
        return Origins.IsSecure(pageOrigin) ? question
            : question + "\n\nAtenção: a ligação a esta página não é segura (http). Quem estiver na mesma rede pode ver a palavra-passe.";
    }

    private static string FullOrigin(string origin)
    {
        if (Origins.Normalize(origin) is not { } normal || Origins.IsAndroid(normal)) return origin;
        var uri = new Uri(normal);
        return uri.Scheme + "://" + (Origins.DisplayHost(normal) ?? uri.Host) + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port);
    }

    // As palavras-passe «De outros sites» (escolha à mão): nunca as de android://, e nunca uma de https numa página que não
    // é segura (http fora do próprio computador): credenciais https nunca vão para http (desenho, secção 3.6).
    public static bool OffersElsewhere(string savedOrigin, string pageOrigin)
    {
        if (Origins.Normalize(savedOrigin) is not { } saved || Origins.IsAndroid(saved)) return false;
        return !saved.StartsWith("https://", StringComparison.Ordinal) || Origins.IsSecure(pageOrigin);
    }

    public static bool AcceptsClick(TimeSpan sinceShown) => sinceShown >= ClickGuard;

    // O Enter também espera o guarda: um campo que ganha o foco por script e um «↓ Enter» imediato não preenchem.
    public static bool AcceptsKey(TimeSpan sinceShown) => AcceptsClick(sinceShown);

    // Uma lista que nasceu debaixo do cursor só aceita o clique depois de o cursor ter saído da linha (e voltado).
    public static bool AcceptsPointer(bool rowUnderCursorAtShow, bool cursorLeftSince) => !rowUnderCursorAtShow || cursorLeftSince;

    // Só um deslocamento do cursor (píxeis do ecrã) desde que a lista apareceu conta como «saiu».
    public static bool CountsAsLeaving(double movedPixels) => movedPixels >= PointerSlack;

    // D-5: «Pedir o Windows Hello ao preencher palavras-passe» (deste PC, desligado por omissão).
    public static bool NeedsReauth(bool askHello) => ReauthPolicy.Needs(ReauthPurpose.FillPassword, askHello, askPayments: true);
}
