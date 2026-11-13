using System.Text;

namespace Eteris.Browser.Watching;

// As regras da vigia, sem motor nem disco: normalizar o texto de um pedaço, reconhecer uma visita barrada, transformar
// uma leitura numa verificação e decidir se essa verificação merece um aviso.
//
// - Um desafio anti-robô (ou um salto para uma aplicação) nunca é «mudou» nem «desapareceu»; à 2.ª vez seguida a vigia
//   fica bloqueada (decisão 8: o Éteris não contorna as proteções; passa a ler só quando o utilizador abre a página).
//   Um 429 (demasiados pedidos) ou um 403 (acesso recusado) conta como desafio, tenha o título que tiver.
// - Uma página que não carregou (erro HTTP, sem rede, tempo esgotado, página vazia) é uma falha passageira: as duas
//   primeiras seguidas deixam o estado como estava, a 3.ª dá «Failing», e nunca bloqueia nem dá «desapareceu».
// - O pedaço (ou o preço) que não aparece numa página que carregou dá «Failing»; à 2.ª vez seguida, «Missing». Se a
//   página carregou noutro endereço (um salto para o login, o consentimento ou uma verificação anti-robô servida com
//   200), não é a página vigiada: é uma falha passageira, nunca «Missing».
// - «Blocked» e «Missing» só mudam com uma leitura boa (a leitura passiva): uma falha deixa-as como estão.
// - Failures conta as verificações falhadas seguidas (IsFailure) e volta a 0 com uma boa; quem grava (WatchStore) e a
//   agenda (WatchSchedule.Next) usam a mesma contagem.
public static class WatchRules
{
    // Títulos das páginas de desafio (sem distinguir maiúsculas); só contam com 503 (o 403 e o 429 já são desafio).
    private static readonly string[] ChallengeTitles =
        ["um momento", "just a moment", "attention required", "verificação de segurança", "are you a robot", "captcha"];

    // Esquemas que não são «o site mandou para uma aplicação»: páginas do próprio motor ou documentos locais.
    private static readonly string[] EngineSchemes = ["http", "https", "about", "data", "blob", "chrome"];

    // Falhas passageiras seguidas que passam a vigia a «Failing».
    private const int LoadFailuresBeforeFailing = 3;

    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            // Os invisíveis (espaço de largura zero, junções, BOM, hífen condicional) somem sem deixar espaço.
            if (c is '​' or '‌' or '‍' or '⁠' or '﻿' or '­') continue;
            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    public static PageBlock Classify(int httpStatus, string requestedUrl, string finalUrl, string title, bool challengeMarker)
    {
        if (challengeMarker) return PageBlock.Challenge;
        // O site recusa a visita (403) ou pede para abrandar (429): é ele a barrar as visitas automáticas, conheça-se ou
        // não o título (PerimeterX, Akamai, «Too Many Requests»). À 2.ª vez seguida a vigia fica bloqueada.
        if (httpStatus is 403 or 429) return PageBlock.Challenge;
        if (httpStatus == 503 && LooksLikeChallenge(title)) return PageBlock.Challenge;

        // A página de erro do motor (sem rede, nome que não resolve) é uma falha de carga, não uma aplicação.
        if (IsEngineError(finalUrl)) return PageBlock.HttpError;
        if (IsAppScheme(finalUrl)) return PageBlock.AppRedirect;

        return httpStatus >= 400 ? PageBlock.HttpError : PageBlock.None;
    }

    // Um endereço fora da web (meli://, mailto:, um programa, uma página interna do Éteris): o motor da vigia cancela a
    // navegação e conta-a como salto para uma aplicação. A página de erro do motor não conta.
    public static bool IsAppScheme(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && !IsEngineError(url)
           && !EngineSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase);

    // A espera do motor da vigia acaba quando a leitura já serve: o preço (numa vigia de preço — um pedaço ainda com o
    // marcador ou vazio não serve), o pedaço com texto (numa vigia de mudança), ou uma página de desafio (esperar mais
    // não a faz passar, e o Éteris nunca a contorna).
    public static bool Settled(WatchKind kind, PageReading reading)
    {
        if (reading.ChallengeMarker) return true;
        if (kind == WatchKind.Price) return reading.Price is not null;
        return reading.PieceFound && !string.IsNullOrWhiteSpace(reading.PieceText);
    }

    private static bool IsEngineError(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme.Equals("chrome-error", StringComparison.OrdinalIgnoreCase);

    public static WatchCheck ToCheck(PageWatch w, PageReading? reading, PageBlock block, DateTime nowUtc)
    {
        if (reading?.ChallengeMarker == true) block = PageBlock.Challenge;
        // Nada lido, ou uma página sem título, sem preço e sem pedaço: a página não carregou.
        if (block == PageBlock.None && (reading is null || IsEmpty(reading))) block = PageBlock.HttpError;

        if (block != PageBlock.None) return Failed(w, block, nowUtc);

        if (w.Kind == WatchKind.Price)
        {
            var price = reading!.Price;
            if (price is null && reading.PieceFound && PriceText.Parse(reading.PieceText) is { } fromPiece)
                price = new PriceReading(fromPiece, w.Currency, "texto");
            if (price is null) return Elsewhere(w, reading) ? Failed(w, PageBlock.HttpError, nowUtc) : NotFound(w, nowUtc);
            return new WatchCheck(nowUtc, Good(w), price.Value, price.Currency ?? w.Currency, null, PageBlock.None);
        }

        if (!reading!.PieceFound) return Elsewhere(w, reading) ? Failed(w, PageBlock.HttpError, nowUtc) : NotFound(w, nowUtc);
        return new WatchCheck(nowUtc, Good(w), null, null, Normalize(reading.PieceText), PageBlock.None);
    }

    public static WatchOutcome Outcome(PageWatch before, WatchCheck check)
    {
        if (check.Status == WatchStatus.Blocked && before.Status != WatchStatus.Blocked) return WatchOutcome.BecameBlocked;
        if (check.Status == WatchStatus.Missing && before.Status != WatchStatus.Missing) return WatchOutcome.PieceMissing;
        if (check.Block != PageBlock.None || check.Status is WatchStatus.Failing or WatchStatus.Missing) return WatchOutcome.Nothing;

        if (before.Kind == WatchKind.Change)
        {
            // A primeira leitura nunca é uma mudança.
            if (before.LastText is null || check.Text is null) return WatchOutcome.Nothing;
            return Normalize(before.LastText) == check.Text ? WatchOutcome.Nothing : WatchOutcome.Changed;
        }

        if (before.LastPrice is not { } old || check.Price is not { } now) return WatchOutcome.Nothing;
        // Preços em moedas diferentes não se comparam.
        if (before.Currency is not null && check.Currency is not null
            && !before.Currency.Equals(check.Currency, StringComparison.OrdinalIgnoreCase)) return WatchOutcome.Nothing;

        if (before.TargetPrice is { } target)
        {
            if (old > target && now <= target) return WatchOutcome.ReachedTarget;
            // Com um alvo, só conta uma descida dentro dele (abaixo do alvo, já atravessado).
            return now < old && now <= target ? WatchOutcome.PriceDropped : WatchOutcome.Nothing;
        }
        return now < old ? WatchOutcome.PriceDropped : WatchOutcome.Nothing;
    }

    // O endereço de uma vigia (na criação e na leitura passiva, que procura pelo endereço exato): sem a âncora (#avaliacoes,
    // #:~:text=…), que é a mesma página noutro ponto; o fragmento de rota de uma aplicação de uma página (#/…, #!…) é
    // outra página e fica.
    public static string PageUrl(string? url)
    {
        if (url is null) return "";
        var hash = url.IndexOf('#');
        if (hash < 0) return url;
        var fragment = url.AsSpan(hash + 1);
        return fragment.StartsWith("/") || fragment.StartsWith("!") ? url : url[..hash];
    }

    // A leitura passiva (o utilizador abriu a página numa aba): só uma leitura boa se grava — o preço numa vigia de preço,
    // o pedaço com texto numa vigia de mudança. A aba pode estar a meio de carregar, rolada ou num desafio que o utilizador
    // vai resolver: nada disso conta como falha, desaparecimento ou bloqueio, e a vigia fica como estava (null).
    public static WatchCheck? Passive(PageWatch w, PageReading? reading, PageBlock block, DateTime nowUtc)
    {
        if (block != PageBlock.None || reading is null || reading.ChallengeMarker) return null;
        var check = ToCheck(w, reading, block, nowUtc);
        if (IsFailure(check)) return null;
        var good = w.Kind == WatchKind.Price ? check.Price is not null : !string.IsNullOrEmpty(check.Text);
        return good ? check : null;
    }

    // Uma verificação falhada: conta para Failures (e para o recuo da agenda).
    public static bool IsFailure(WatchCheck check)
        => check.Block != PageBlock.None || check.Status is WatchStatus.Failing or WatchStatus.Missing;

    // A página carregou noutro endereço: outro site ou outro caminho (o login, o consentimento, /errors/validateCaptcha).
    // O mesmo caminho com outro esquema, sem «www.», com a barra final, com outra consulta ou noutra âncora é a mesma
    // página. Um endereço que não se lê conta como a mesma (a falta conta como sempre contou).
    private static bool Elsewhere(PageWatch w, PageReading reading)
    {
        if (!Uri.TryCreate(PageUrl(w.Url), UriKind.Absolute, out var watched)
            || !Uri.TryCreate(PageUrl(reading.FinalUrl), UriKind.Absolute, out var final)) return false;
        static string Host(Uri u) => u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;
        static string Path(Uri u) => u.AbsolutePath.TrimEnd('/');
        return !Host(watched).Equals(Host(final), StringComparison.OrdinalIgnoreCase)
               || !Path(watched).Equals(Path(final), StringComparison.OrdinalIgnoreCase)
               || watched.Fragment != final.Fragment;
    }

    private static bool LooksLikeChallenge(string? title)
        => !string.IsNullOrEmpty(title) && ChallengeTitles.Any(t => title.Contains(t, StringComparison.OrdinalIgnoreCase));

    private static bool IsEmpty(PageReading r)
        => r.Price is null && !r.PieceFound && string.IsNullOrWhiteSpace(r.Title);

    // Uma leitura boa tira a vigia de «Failing» ou «Missing»; uma bloqueada continua bloqueada (só a leitura passiva a lê).
    private static WatchStatus Good(PageWatch w) => w.Status == WatchStatus.Blocked ? WatchStatus.Blocked : WatchStatus.Ok;

    private static WatchCheck Failed(PageWatch w, PageBlock block, DateTime nowUtc)
    {
        WatchStatus status;
        // Bloqueada e sem pedaço só saem com uma leitura boa: uma falha não as devolve à agenda.
        if (w.Status is WatchStatus.Blocked or WatchStatus.Missing) status = w.Status;
        else if (block is PageBlock.Challenge or PageBlock.AppRedirect)
            status = w.LastBlock == block ? WatchStatus.Blocked : WatchStatus.Failing;
        else
        {
            var inARow = w.LastBlock == PageBlock.HttpError ? w.Failures + 1 : 1;
            status = inARow >= LoadFailuresBeforeFailing ? WatchStatus.Failing : w.Status;
        }
        return new WatchCheck(nowUtc, status, null, null, null, block);
    }

    // O preço ou o pedaço não está numa página que carregou: à 2.ª vez seguida, desapareceu.
    private static WatchCheck NotFound(PageWatch w, DateTime nowUtc)
    {
        var status = w.Status switch
        {
            WatchStatus.Blocked or WatchStatus.Missing => w.Status,
            WatchStatus.Failing when w.LastBlock == PageBlock.None => WatchStatus.Missing,
            _ => WatchStatus.Failing,
        };
        return new WatchCheck(nowUtc, status, null, null, null, PageBlock.None);
    }
}
