using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace Eteris.Browser.Browser;

public sealed record ErrorDescription(string Title, string Detail, string Glyph);

public static class ErrorPage
{
    private const string Prefix = "eteris://error/";

    private static readonly Lazy<string> Template = new(() =>
    {
        using var stream = typeof(ErrorPage).Assembly.GetManifestResourceStream("Eteris.Browser.Browser.error.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    // Código interno (não é do Chromium) para quando o processo da página termina inesperadamente.
    public const int RendererCrashed = ErrorDiagnosis.RendererCrashed;

    // -3 (ERR_ABORTED) acontece em transferências e ao parar: não é um erro para mostrar.
    public static bool ShouldShow(int code) => code != -3;

    public static ErrorDescription Describe(int code)
    {
        var diagnosis = ErrorDiagnosis.For(code);
        return new(diagnosis.Title, diagnosis.Detail, diagnosis.Glyph);
    }

    // O que carregar quando o documento principal falha — nas abas e nos sites fixados da barra lateral. Quando foi o
    // «HTTPS primeiro» a subir o pedido para https:// (failedUpgrade devolve o http:// original) e o site não fala
    // HTTPS, o aviso «Este site não oferece ligação segura» — nunca o recuo silencioso para http://; nas outras
    // falhas desse pedido, a página de erro com o endereço que a pessoa abriu. Null quando não há nada a mostrar
    // (parado, transferência, ou a própria página de erro a falhar).
    // allowed: a pessoa já escolheu continuar neste site. Se mesmo assim o https:// falhou, foi o próprio http:// que
    // levou de volta ao https:// (redirecionamento do servidor ou HSTS): é uma falha de https:// como outra qualquer,
    // sem «Continuar» — voltar a abrir o http:// repetia o ciclo sem fim.
    public static string? Recovery(int code, string failedUrl, Func<string, string?> failedUpgrade, Func<string, bool>? allowed = null)
    {
        if (IsErrorPage(failedUrl)) return null;
        var original = failedUpgrade(failedUrl);
        // Demasiados redirecionamentos (o https:// manda para http://, que volta a ser subido…): o Chromium pode
        // desistir em qualquer um dos dois endereços.
        if (original is null && code == -310 && Uri.TryCreate(failedUrl, UriKind.Absolute, out var loop) && loop.Scheme == Uri.UriSchemeHttp)
            original = failedUpgrade(new UriBuilder(loop) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.ToString());
        if (original is not null)
        {
            if (allowed?.Invoke(original) == true) return ShouldShow(code) ? Url(code, failedUrl) : null;
            if (Shields.HttpsFirst.IsUpgradeFailure(code)) return InsecureUrl(code, original);
            failedUrl = original;
        }
        return ShouldShow(code) ? Url(code, failedUrl) : null;
    }

    // A navegação que falhou foi um passo do histórico («Voltar», Alt+←, o botão lateral do rato) a partir da página
    // de erro desse mesmo endereço — ou do aviso, da sua página «Continuar», ou do http:// aceite. A entrada que
    // falhou só existe por trás dessa página: em vez de voltar a mostrar o erro (e cortar o histórico, prendendo a
    // pessoa ali), segue-se mais um passo para trás, como no Chrome, no Firefox e no Brave.
    // afterSkip: este passo já foi um salto destes — a entrada anterior também é uma falha do mesmo endereço (o http://
    // aceite que voltou ao https://), e salta-se também.
    public static bool SkipsBack(bool historyNavigation, string? cameFrom, string failedUrl, bool afterSkip = false)
    {
        if (!historyNavigation || cameFrom is null || IsErrorPage(failedUrl)) return false;
        if (IsErrorPage(cameFrom)) return FailedUrl(cameFrom) is { } shown && SameAddress(shown, failedUrl);
        if (afterSkip) return SameAddress(cameFrom, failedUrl);
        return cameFrom.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               && failedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && SameAddress(cameFrom, failedUrl);
    }

    // O mesmo endereço, com http:// ou https:// (o aviso mostra o http:// que a pessoa abriu; falhou o https://).
    public static bool SameAddress(string a, string b)
        => string.Equals(WithoutScheme(a), WithoutScheme(b), StringComparison.OrdinalIgnoreCase);

    private static string WithoutScheme(string url)
        => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url[8..]
           : url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? url[7..] : url;

    public static string Url(int code, string failedUrl) => $"{Prefix}?code={code}&url={Uri.EscapeDataString(failedUrl)}";

    // O aviso do «HTTPS primeiro»: é a página de erro com a marca https=1 (a omnibox, o F5 e o «Abrir numa aba»
    // dos painéis tratam-na como qualquer erro: o endereço é o http:// que a pessoa abriu).
    public static string InsecureUrl(int code, string httpUrl) => Url(code, httpUrl) + "&https=1";

    public static bool IsInsecureWarning(string address)
        => IsErrorPage(address) && Uri.TryCreate(address, UriKind.Absolute, out var uri)
           && HttpUtility.ParseQueryString(uri.Query)["https"] == "1";

    // Chave só desta sessão: assina cada «Continuar» (a página de erro não leva o token das outras rotas internas).
    private static readonly byte[] ContinueKey = RandomNumberGenerator.GetBytes(32);

    private static string Sign(string httpUrl, bool always)
        => Convert.ToHexString(HMACSHA256.HashData(ContinueKey, Encoding.UTF8.GetBytes((always ? "1" : "0") + "\n" + httpUrl)));

    // «Continuar sem segurança» e «Sempre para este site»: eteris://error/continue com a assinatura do endereço e da
    // escolha (s) — uma ligação adulterada, ou copiada para outro endereço, deixa de valer.
    public static string ContinueUrl(string httpUrl, bool always)
        => $"{Prefix}continue?url={Uri.EscapeDataString(httpUrl)}{(always ? "&always=1" : string.Empty)}&s={Sign(httpUrl, always)}";

    public sealed record InsecureChoice(string Url, string Host, bool Always);

    // Lê o pedido de eteris://error/continue: só com a assinatura certa e só para um http:// (nunca outro esquema).
    public static InsecureChoice? ParseContinue(string requestUrl)
    {
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri) || uri.AbsolutePath != "/continue"
            || !requestUrl.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var query = HttpUtility.ParseQueryString(uri.Query);
        var always = query["always"] == "1";
        if (query["url"] is not { } url || query["s"] is not { } signature) return null;
        var expected = Encoding.UTF8.GetBytes(Sign(url, always));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(signature), expected)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) || target.Scheme != Uri.UriSchemeHttp) return null;
        return new InsecureChoice(target.AbsoluteUri, target.Host, always);
    }

    // Depois de aceitar: a página segue para o http:// e substitui o aviso no histórico (o «Voltar» não o repete).
    public static string ContinueRedirect(string httpUrl)
        => "<!doctype html><html lang=\"pt\"><head><meta charset=\"utf-8\">"
           + "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; script-src 'unsafe-inline'\">"
           + "<meta name=\"color-scheme\" content=\"dark\"></head><body><script>location.replace("
           + JsonSerializer.Serialize(httpUrl) + ");</script></body></html>";

    public static bool IsErrorPage(string address) => address.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static string? FailedUrl(string address)
        => IsErrorPage(address) && Uri.TryCreate(address, UriKind.Absolute, out var uri)
            ? HttpUtility.ParseQueryString(uri.Query)["url"]
            : null;

    // O que um recarregamento pode voltar a abrir a partir de uma página de erro: o parâmetro «url» é texto de quem
    // escreveu o endereço (ou de uma página que mandou abrir eteris://error/…), por isso só um endereço http, https
    // ou de ficheiro volta a ser carregado — nunca javascript:, data:, eteris:// ou outro esquema. Null quando não há
    // alvo válido: quem chama fica com a própria página de erro (browser.Reload) ou com o endereço que já tinha.
    public static string? ReloadTarget(string address)
        => FailedUrl(address) is { } failed && Uri.TryCreate(failed, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFile)
            ? failed
            : null;

    public static int Code(string address)
        => IsErrorPage(address) && Uri.TryCreate(address, UriKind.Absolute, out var uri)
           && int.TryParse(HttpUtility.ParseQueryString(uri.Query)["code"], out var code)
            ? code
            : 0;

    // secureDns: o DNS seguro está ligado (o diagnóstico de «nome não resolve» sugere desligá-lo se a rede o bloqueia).
    public static string Render(int code, string failedUrl, bool insecure = false, bool canRemember = true, bool secureDns = false)
    {
        if (insecure) return RenderInsecure(code, failedUrl, canRemember);
        var diagnosis = ErrorDiagnosis.For(code, secureDns);
        var retry = failedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || failedUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        var data = new
        {
            title = diagnosis.Title,
            detail = diagnosis.Detail,
            glyph = diagnosis.Glyph,
            url = failedUrl,
            retry,
            offline = diagnosis.Kind == ErrorKind.Offline,
            code,
            // Diagnóstico legível («O nome não resolve (DNS)»), o que fazer, e o texto de «Copiar detalhes».
            cause = diagnosis.Cause,
            name = diagnosis.Name,
            tips = diagnosis.Tips,
            details = ErrorDiagnosis.Details(code, failedUrl, SettingsPage.Version, DateTime.Now),
        };
        return Template.Value.Replace("{{DATA}}", JsonSerializer.Serialize(data));
    }

    // «Este site não oferece ligação segura» (como o Firefox e o Brave): Voltar é o botão principal; continuar é uma
    // escolha consciente, só desta vez (até fechar o Éteris) ou sempre para este site (nunca na janela privada).
    private static string RenderInsecure(int code, string httpUrl, bool canRemember)
    {
        var host = Uri.TryCreate(httpUrl, UriKind.Absolute, out var uri) ? uri.Host : httpUrl;
        var data = new
        {
            title = "Este site não oferece ligação segura",
            detail = $"O Éteris tentou abrir {host} com ligação cifrada (HTTPS), mas o site não respondeu assim. Se continuar, a página "
                     + "chega sem cifra: quem estiver na mesma rede (um Wi-Fi público, o fornecedor de Internet) pode ver o que lê e escreve "
                     + "e alterar a página pelo caminho.",
            glyph = "🔓",
            url = httpUrl,
            retry = false,
            offline = false,
            code,
            insecure = true,
            cause = "O site não fala HTTPS",
            name = ErrorDiagnosis.NameOf(code),
            tips = Array.Empty<string>(),
            details = ErrorDiagnosis.Details(code, httpUrl, SettingsPage.Version, DateTime.Now),
            proceedOnce = ContinueUrl(httpUrl, always: false),
            proceedAlways = canRemember ? ContinueUrl(httpUrl, always: true) : null,
        };
        return Template.Value.Replace("{{DATA}}", JsonSerializer.Serialize(data));
    }
}
