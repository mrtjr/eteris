namespace Eteris.Browser.Vault;

// Um envio de formulário com palavra-passe (o «submit» do script), à espera de saber se correu bem. Vive só na memória,
// preso à aba (MainWindow.Vault.cs), e nunca chega ao cofre: só a escolha do utilizador na bolha guarda alguma coisa. A
// origem é a do documento no processo do navegador (VaultContext), nunca a que a página diz.
public sealed record SaveCandidate(string Origin, string FormKind, string Form, int ContextId, string Username, string? Password, string? NewPassword, DateTimeOffset At)
{
    public override string ToString() => $"SaveCandidate {FormKind} {Form}";
}

// Uma palavra-passe já guardada para a mesma origem (para decidir entre guardar, atualizar ou nada).
public sealed record StoredLogin(string Id, string Username, string Password, bool Generated)
{
    public override string ToString() => $"StoredLogin {Id}";
}

public enum SaveOfferKind { None, Save, Update, ConfirmGenerated }

public enum SubmitOutcome { Pending, Success, Failed }

// O que a bolha pergunta (ou, numa palavra-passe gerada, o que se confirma sem perguntar).
public sealed record SaveOffer(SaveOfferKind Kind, string Origin, string Username, string Password, string? ExistingId)
{
    public static SaveOffer Nothing(string origin) => new(SaveOfferKind.None, origin, string.Empty, string.Empty, null);

    public override string ToString() => $"SaveOffer {Kind} {ExistingId}";
}

// Guardar, atualizar e «Nunca neste site» (docs/palavras-passe.md, C4; desenho, secção 3.7). Decisões puras.
public static class SavePolicy
{
    // O candidato larga-se ao fim de 5 minutos (ou ao fechar a aba).
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    // Uma navegação (ou o formulário a desaparecer) só conta como resultado do envio até 20 s depois dele: mais tarde é o
    // utilizador a ir para outro lado depois de um erro.
    public static readonly TimeSpan SuccessWindow = TimeSpan.FromSeconds(20);

    // A oferta (e o ícone de chave) passam à navegação que a trouxe e aos redirecionamentos logo a seguir.
    public static readonly TimeSpan RedirectGrace = TimeSpan.FromSeconds(3);

    public const int MaxPassword = 512;

    private static readonly HashSet<string> s_formKinds = ["login", "signup", "change"];

    // O candidato de um «submit»: só em páginas http(s), com «Oferecer para guardar palavras-passe» ligado, fora da janela
    // privada (D-3), num início de sessão, criação de conta ou mudança, com uma palavra-passe que não seja gigante.
    public static SaveCandidate? Candidate(VaultMessage message, VaultContext context, DateTimeOffset now, bool offerEnabled, bool isPrivate)
    {
        if (!offerEnabled || isPrivate || message.Type != VaultMessageType.Submit) return null;
        if (Origins.Normalize(context.Origin) is not { } origin || Origins.IsAndroid(origin)) return null;
        if (message.FormKind is not { } formKind || !s_formKinds.Contains(formKind) || message.Form is not { } form) return null;
        var candidate = new SaveCandidate(origin, formKind, form, context.Id, message.Username ?? string.Empty, message.Password, message.NewPassword, now);
        return FinalPassword(candidate) is { Length: > 0 and <= MaxPassword } ? candidate : null;
    }

    // A palavra-passe que fica: a nova numa criação de conta ou mudança, a escrita num início de sessão.
    public static string? FinalPassword(SaveCandidate c) => c.FormKind == "login" ? c.Password : c.NewPassword;

    public static bool Expired(SaveCandidate c, DateTimeOffset now) => now - c.At > Lifetime;

    // O frame principal navegou: correu bem, a não ser que o início de sessão volte na mesma origem (erro de login).
    public static SubmitOutcome AfterNavigation(SaveCandidate c, string? newOrigin, bool loginFormAgain, DateTimeOffset now)
    {
        if (now - c.At > SuccessWindow) return SubmitOutcome.Failed;
        return loginFormAgain && Origins.Normalize(newOrigin) == c.Origin ? SubmitOutcome.Failed : SubmitOutcome.Success;
    }

    // O formulário do candidato desapareceu sem voltar com a palavra-passe (SPA).
    public static SubmitOutcome AfterFormGone(SaveCandidate c, int contextId, string form, DateTimeOffset now)
    {
        if (contextId != c.ContextId || form != c.Form) return SubmitOutcome.Pending;
        return now - c.At > SuccessWindow ? SubmitOutcome.Failed : SubmitOutcome.Success;
    }

    // Guardar, atualizar, confirmar uma gerada, ou nada (site «nunca», igual ao guardado).
    public static SaveOffer Decide(SaveCandidate c, bool neverSite, IReadOnlyList<StoredLogin> sameOrigin)
    {
        var password = FinalPassword(c);
        if (neverSite || string.IsNullOrEmpty(password) || password.Length > MaxPassword) return SaveOffer.Nothing(c.Origin);
        // Uma gerada pelo Éteris à espera deste envio: confirma-se (com o utilizador do formulário, se o tiver).
        if (sameOrigin.FirstOrDefault(s => s.Generated && s.Password == password) is { } generated)
            return new(SaveOfferKind.ConfirmGenerated, c.Origin, c.Username.Length > 0 ? c.Username : generated.Username, password, generated.Id);

        StoredLogin? existing;
        if (c.FormKind == "change")
        {
            existing = (c.Username.Length > 0 ? sameOrigin.FirstOrDefault(s => s.Username == c.Username) : null)
                ?? sameOrigin.FirstOrDefault(s => !string.IsNullOrEmpty(c.Password) && s.Password == c.Password)
                ?? (sameOrigin.Count == 1 ? sameOrigin[0] : null);
        }
        else
        {
            existing = sameOrigin.FirstOrDefault(s => s.Username == c.Username);
        }
        if (existing is null) return new(SaveOfferKind.Save, c.Origin, c.Username, password, null);
        if (existing.Password == password) return SaveOffer.Nothing(c.Origin);
        return new(SaveOfferKind.Update, c.Origin, existing.Username, password, existing.Id);
    }

    // A pergunta da bolha, com a origem do processo do navegador.
    public static string Question(SaveOffer offer)
    {
        var host = Origins.DisplayHost(offer.Origin) ?? offer.Origin;
        return offer.Kind == SaveOfferKind.Update && offer.Username.Length > 0
            ? $"Atualizar a palavra-passe de {offer.Username} em {host}?"
            : offer.Kind == SaveOfferKind.Update ? $"Atualizar a palavra-passe de {host}?" : $"Guardar a palavra-passe de {host}?";
    }

    public static bool KeepsOfferAcrossNavigation(TimeSpan sinceOffer) => sinceOffer <= RedirectGrace;
}
