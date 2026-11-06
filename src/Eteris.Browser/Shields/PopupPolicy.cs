namespace Eteris.Browser.Shields;

// O último clique (ou Enter) do utilizador na página: para onde apontava o link ou o formulário em que carregou.
// LinkUrl vazio = carregou em algo que não é link (texto, imagem, fundo da página…).
public sealed record UserActivation(string LinkUrl, DateTime AtUtc)
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    public bool IsRecent(DateTime nowUtc) => nowUtc - AtUtc <= Window;
}

public static class PopupPolicy
{
    // Como o Chrome: sem gesto do utilizador não há pop-up. Com gesto, o destino passa pelas listas como
    // navegação, porque o adblock-rust ainda não implementa as regras $popup (apanha os redirecionadores ||dominio^).
    //
    // Pop-unders: o script de anúncios espera por um clique qualquer na página e abre o anúncio numa aba — às vezes
    // por um formulário escondido ou pelo window.open de um iframe novo, para fugir a quem vigia window.open. Por isso
    // a decisão é tomada aqui, no motor: uma aba nova noutro site só abre se o clique foi num link (ou formulário)
    // para esse site. As janelas pop-up com dimensões (início de sessão, partilhar) continuam a abrir.
    // Sem registo do clique (o clique foi dentro de um iframe de outro site) fica só a regra das listas.
    public static bool Allow(bool userGesture, string targetUrl, string? openerUrl, IRequestBlocker blocker, Func<string, bool> isProtected,
        UserActivation? activation = null, bool isPopupWindow = false, DateTime? nowUtc = null)
    {
        if (!userGesture) return false;
        if (openerUrl is not null && Uri.TryCreate(openerUrl, UriKind.Absolute, out var opener) && !isProtected(opener.Host))
            return true;
        if (blocker.ShouldBlock(targetUrl, openerUrl, "document")) return false;
        if (isPopupWindow || activation is null || !activation.IsRecent(nowUtc ?? DateTime.UtcNow)) return true;
        return SameSite(targetUrl, openerUrl) || SameSite(targetUrl, activation.LinkUrl);
    }

    public const string ActivationPrefix = "\u0001eteris-click ";

    public static bool IsActivationReport(string? message, out string linkUrl)
    {
        linkUrl = string.Empty;
        if (message is null || !message.StartsWith(ActivationPrefix, StringComparison.Ordinal)) return false;
        linkUrl = message[ActivationPrefix.Length..];
        return true;
    }

    // Corre antes dos scripts da página, em todos os frames: regista onde o utilizador carregou. O nosso ouvinte, em
    // captura na janela, é o primeiro a ser registado, por isso a página não o consegue calar.
    public const string ActivationScript = """
        (() => {
          const debug = console.debug.bind(console);
          const target = e => {
            for (const node of (e.composedPath ? e.composedPath() : [e.target])) {
              if (!(node instanceof Element)) continue;
              if ((node.tagName === 'A' || node.tagName === 'AREA') && node.href) return String(node.href);
              if (node.form && (node.tagName === 'BUTTON' || node.tagName === 'INPUT')) return String(node.formAction || node.form.action || '');
            }
            return '';
          };
          const report = e => { if (e.isTrusted) debug('\u0001eteris-click ' + target(e)); };
          addEventListener('pointerdown', report, true);
          addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') report(e); }, true);
        })();
        """;

    private static bool SameSite(string? a, string? b)
        => IsWeb(a, out var ua) && IsWeb(b, out var ub)
           && TrackerBlocker.RegistrableDomain(ua.Host.ToLowerInvariant()) == TrackerBlocker.RegistrableDomain(ub.Host.ToLowerInvariant());

    private static bool IsWeb(string? url, out Uri uri)
        => Uri.TryCreate(url, UriKind.Absolute, out uri!) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
