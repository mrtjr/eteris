namespace Eteris.Browser.Shields;

// Um pedido de pop-up/aba nova feito por uma página (window.open, formulário ou link com target=_blank).
public sealed record PopupRequest(string TargetUrl, string? OpenerUrl, bool UserGesture, bool IsPopupWindow, bool Foreground, DateTime AtUtc);

// O último clique (ou Enter) do utilizador na página: para onde apontava o link ou o formulário em que carregou, e
// se carregou num controlo (botão, campo, elemento com papel de botão/link) ou em conteúdo (texto, imagem, fundo).
public sealed record UserActivation(string LinkUrl, DateTime AtUtc, bool Interactive = false)
{
    // O pedido de pop-up pode chegar ao motor um pouco antes do aviso do clique (os scripts de anúncios abrem
    // logo ao carregar no botão do rato): conta um clique até 2 s antes e até 1 s depois do pedido.
    public bool IsAround(DateTime requestUtc) => requestUtc - AtUtc <= TimeSpan.FromSeconds(2) && AtUtc - requestUtc <= TimeSpan.FromSeconds(1);
}

public static class PopupPolicy
{
    // Tempo que a janela espera pelo aviso do clique antes de decidir um pop-up.
    public static readonly TimeSpan ActivationWait = TimeSpan.FromMilliseconds(250);

    // Como o Chrome: sem gesto do utilizador não há pop-up. Com gesto, o destino passa pelas listas como
    // navegação, porque o adblock-rust ainda não implementa as regras $popup (apanha os redirecionadores ||dominio^).
    //
    // Pop-unders: o script de anúncios espera por um clique qualquer na página e abre o anúncio numa aba — às vezes
    // por um formulário escondido ou pelo window.open de um iframe novo, para fugir a quem vigia window.open. Por isso
    // a decisão é tomada aqui, no motor: uma aba nova noutro site só abre se o clique foi num link (ou formulário)
    // para esse site. Janelas pop-up com dimensões (início de sessão, partilhar) abrem se o clique foi num botão ou
    // link — nunca num clique numa imagem ou no fundo da página, que é o que os anúncios aproveitam.
    // Sem registo do clique (o clique foi dentro de um iframe de outro site) fica só a regra das listas.
    public static bool Allow(bool userGesture, string targetUrl, string? openerUrl, IRequestBlocker blocker, Func<string, bool> isProtected,
        UserActivation? activation = null, bool isPopupWindow = false, DateTime? nowUtc = null)
    {
        if (!userGesture) return false;
        if (!Browser.PageLinks.CanOpenPopup(targetUrl, openerUrl)) return false;
        if (openerUrl is not null && Uri.TryCreate(openerUrl, UriKind.Absolute, out var opener) && !isProtected(opener.Host))
            return true;
        if (blocker.ShouldBlock(targetUrl, openerUrl, "document")) return false;
        if (activation is null || !activation.IsAround(nowUtc ?? DateTime.UtcNow)) return true;
        if (SameSite(targetUrl, openerUrl) || SameSite(targetUrl, activation.LinkUrl)) return true;
        return isPopupWindow && activation.Interactive;
    }

    public static bool Allow(PopupRequest request, IRequestBlocker blocker, Func<string, bool> isProtected, UserActivation? activation)
        => Allow(request.UserGesture, request.TargetUrl, request.OpenerUrl, blocker, isProtected, activation, request.IsPopupWindow, request.AtUtc);

    // Segredo deste processo, fechado no script do clique: qualquer página pode escrever na consola, mas só o nosso
    // ouvinte (que corre antes dela, em funções estritas — sem .caller — e nunca entrega o segredo a código da página)
    // o conhece. Sem ele, uma mensagem não é um clique: nem para os pop-ups, nem para «visitado a sério» nos saltos.
    public static readonly string ActivationNonce = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    public static string ActivationPrefix { get; } = "\u0001eteris-click " + ActivationNonce + " ";

    // Formato: prefixo (com o segredo) + «i» (controlo) ou «-» (conteúdo) + endereço do link/formulário (vazio se não houver).
    public static bool IsActivationReport(string? message, out string linkUrl, out bool interactive)
    {
        linkUrl = string.Empty;
        interactive = false;
        if (message is null || !message.StartsWith(ActivationPrefix, StringComparison.Ordinal) || message.Length <= ActivationPrefix.Length)
            return false;
        interactive = message[ActivationPrefix.Length] == 'i';
        linkUrl = message[(ActivationPrefix.Length + 1)..];
        return true;
    }

    // Corre antes dos scripts da página, em todos os frames: regista onde o utilizador carregou. O nosso ouvinte, em
    // captura na janela, é o primeiro a ser registado, por isso a página não o consegue calar.
    public static string ActivationScript { get; } = ActivationScriptFor(ActivationNonce);

    internal static string ActivationScriptFor(string nonce) => ActivationTemplate.Replace("__NONCE__", nonce, StringComparison.Ordinal);

    private const string ActivationTemplate = """
        (() => {
          'use strict';
          const debug = console.debug.bind(console);
          const prefix = '\u0001eteris-click __NONCE__ ';
          const controls = new Set(['BUTTON', 'INPUT', 'SELECT', 'TEXTAREA', 'SUMMARY', 'A', 'AREA']);
          const roles = new Set(['button', 'link', 'menuitem', 'tab', 'checkbox', 'switch', 'option']);
          const describe = e => {
            let link = '', interactive = false;
            for (const node of (e.composedPath ? e.composedPath() : [e.target])) {
              if (!(node instanceof Element)) continue;
              if (controls.has(node.tagName) || roles.has(node.getAttribute('role'))) interactive = true;
              if (!link && (node.tagName === 'A' || node.tagName === 'AREA') && node.href) link = String(node.href);
              if (!link && node.form && (node.tagName === 'BUTTON' || node.tagName === 'INPUT')) link = String(node.formAction || node.form.action || '');
            }
            return (interactive ? 'i' : '-') + link;
          };
          const report = e => { if (e.isTrusted) debug(prefix + describe(e)); };
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
