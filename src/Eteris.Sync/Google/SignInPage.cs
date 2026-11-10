namespace Eteris.Sync.Google;

// O que a página de fim diz, entre os casos previstos. Escolhe-o quem sabe como ficou a sincronização (o Éteris, depois
// de trocar o código e de ler a pasta do Drive); na dúvida, a neutra.
public enum SignInPageKind
{
    FirstDevice,   // o Drive não tinha conta: este é o 1.º computador e a sincronização começou
    NeedsApproval, // o Drive já tem conta e este computador não tem a chave: falta confirmá-lo noutro
    Resumed,       // a chave já estava guardada neste computador (reentrada)
    DriveDenied,   // a permissão da pasta do Drive foi desmarcada no ecrã da Google
    Neutral,       // passou o tempo, erro de rede, outra conta: não se promete nada
    Refused,       // a autorização não chegou (recusa, troca do código falhada)
}

// A página que o recetor de loopback mostra no navegador do sistema no fim da entrada. HTML autónomo e fixo: sem
// scripts, sem pedidos para fora (o logótipo é SVG embutido) e sem nenhum dado do utilizador — Html só recebe o caso.
public static class SignInPage
{
    public static string? Lead(SignInPageKind kind) => kind switch
    {
        SignInPageKind.FirstDevice or SignInPageKind.Resumed => "Pronto.",
        SignInPageKind.NeedsApproval => "Falta um passo no Éteris.",
        _ => null,
    };

    public static string Body(SignInPageKind kind) => kind switch
    {
        SignInPageKind.FirstDevice => "O Éteris começou a sincronizar. Pode fechar esta aba e voltar ao Éteris.",
        SignInPageKind.NeedsApproval => "Volte ao Éteris: vai confirmar este computador noutro onde já entrou.",
        SignInPageKind.Resumed => "O Éteris retomou a sincronização. Pode fechar esta aba.",
        SignInPageKind.DriveDenied => "A Google não deu ao Éteris acesso à pasta do Drive. Sem isso não há sincronização. Volte ao Éteris e tente outra vez.",
        SignInPageKind.Neutral => "Sessão iniciada. Volte ao Éteris para continuar.",
        _ => "O Éteris não recebeu autorização. Volte ao Éteris e tente outra vez.",
    };

    // Os textos são constantes sem «<», «>» nem «&»: entram tal como estão (codificá-los estragava os acentos à vista
    // dos testes que os procuram).
    public static string Html(SignInPageKind kind)
    {
        var lead = Lead(kind) is { } title ? "<h1>" + title + "</h1>\n" : string.Empty;
        return Head + lead + "<p>" + Body(kind) + "</p>\n</main>\n</body>\n</html>\n";
    }

    // Cores do tema do Éteris (Theme/Light.xaml e Dark.xaml) e o acento íris. O símbolo é um anel com a íris ao centro,
    // desenhado aqui (não há logótipo em SVG no projeto); o SVG embutido em HTML não precisa de «xmlns».
    private const string Head = """
        <!doctype html>
        <html lang="pt-PT">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Éteris</title>
        <style>
        :root { color-scheme: light dark; --bg: #F6F7FB; --card: #FFFFFF; --line: #D5D9E4; --text: #1B1F2A; --muted: #535B6E; --accent: #8B5CF6; }
        @media (prefers-color-scheme: dark) {
          :root { --bg: #1A1D27; --card: #242836; --line: #2B3042; --text: #E9EBF2; --muted: #9097A8; --accent: #A78BFA; }
        }
        html, body { margin: 0; height: 100%; }
        body { display: flex; align-items: center; justify-content: center; background: var(--bg); color: var(--text);
               font: 16px/1.5 "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif; }
        main { box-sizing: border-box; max-width: 460px; margin: 16px; padding: 32px 36px; background: var(--card);
               border: 1px solid var(--line); border-radius: 18px; text-align: center; }
        svg { display: block; margin: 0 auto 16px; color: var(--accent); }
        h1 { margin: 0 0 8px; font-size: 22px; font-weight: 600; }
        p { margin: 0; color: var(--muted); }
        </style>
        </head>
        <body>
        <main>
        <svg width="56" height="56" viewBox="0 0 56 56" aria-hidden="true"><circle cx="28" cy="28" r="24" fill="none" stroke="currentColor" stroke-width="3"/><circle cx="28" cy="28" r="13" fill="none" stroke="currentColor" stroke-width="2" opacity="0.55"/><circle cx="28" cy="28" r="6" fill="currentColor"/></svg>

        """;
}
