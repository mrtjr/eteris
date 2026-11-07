using System.IO;
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
    public const int RendererCrashed = -9001;

    // -3 (ERR_ABORTED) acontece em transferências e ao parar: não é um erro para mostrar.
    public static bool ShouldShow(int code) => code != -3;

    public static ErrorDescription Describe(int code) => code switch
    {
        RendererCrashed => new("Esta página foi abaixo",
            "O processo que a mostrava terminou inesperadamente (falta de memória ou um erro do site). As outras abas não foram afetadas.", "☄"),
        -105 or -137 => new("Não encontrámos este site", "Verifique se o endereço está bem escrito ou tente pesquisá-lo.", "🔎"),
        -106 => new("Está sem ligação à Internet", "Verifique o Wi-Fi ou o cabo. Enquanto isso, apanhe a luz.", "✦"),
        -100 or -101 or -102 or -104 or -109 or -118 or -7 => new("O site não respondeu",
            "Pode estar em baixo ou a demorar demasiado. Tente de novo daqui a pouco.", "⏳"),
        <= -200 and > -300 => new("A ligação não é segura",
            "O certificado deste site não é válido. O Éteris não abriu a página para proteger os seus dados.", "🛡"),
        _ => new("Não foi possível abrir a página", $"O Chromium devolveu o erro {code}.", "⚠"),
    };

    // O que carregar quando o documento principal falha — nas abas e nos sites fixados da barra lateral. Quando foi o
    // «HTTPS primeiro» a subir o pedido para https:// (failedUpgrade devolve o http:// original) e o site não fala
    // HTTPS, o aviso «Este site não oferece ligação segura» — nunca o recuo silencioso para http://; nas outras
    // falhas desse pedido, a página de erro com o endereço que a pessoa abriu. Null quando não há nada a mostrar
    // (parado, transferência, ou a própria página de erro a falhar).
    // allowed: a pessoa já escolheu continuar neste site (voltar atrás até à entrada que falhou vai direto ao http://).
    public static string? Recovery(int code, string failedUrl, Func<string, string?> failedUpgrade, Func<string, bool>? allowed = null)
    {
        if (IsErrorPage(failedUrl)) return null;
        if (failedUpgrade(failedUrl) is { } original)
        {
            if (Shields.HttpsFirst.IsUpgradeFailure(code))
                return allowed?.Invoke(original) == true ? original : InsecureUrl(code, original);
            failedUrl = original;
        }
        return ShouldShow(code) ? Url(code, failedUrl) : null;
    }

    public static string Url(int code, string failedUrl) => $"{Prefix}?code={code}&url={Uri.EscapeDataString(failedUrl)}";

    // O aviso do «HTTPS primeiro»: é a página de erro com a marca https=1 (a omnibox, o F5 e o «Abrir numa aba»
    // dos painéis tratam-na como qualquer erro: o endereço é o http:// que a pessoa abriu).
    public static string InsecureUrl(int code, string httpUrl) => Url(code, httpUrl) + "&https=1";

    public static bool IsInsecureWarning(string address)
        => IsErrorPage(address) && Uri.TryCreate(address, UriKind.Absolute, out var uri)
           && HttpUtility.ParseQueryString(uri.Query)["https"] == "1";

    // «Continuar sem segurança» e «Sempre para este site»: eteris://error/continue com o token desta sessão.
    public static string ContinueUrl(string httpUrl, bool always)
        => $"{Prefix}continue?url={Uri.EscapeDataString(httpUrl)}{(always ? "&always=1" : string.Empty)}&t={InternalPages.Token}";

    public sealed record InsecureChoice(string Url, string Host, bool Always);

    // Lê o pedido de eteris://error/continue: só com o token e só para um http:// (nunca outro esquema).
    public static InsecureChoice? ParseContinue(string requestUrl)
    {
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri) || uri.AbsolutePath != "/continue"
            || !requestUrl.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || !InternalPages.IsAuthorized(requestUrl))
            return null;
        var query = HttpUtility.ParseQueryString(uri.Query);
        if (!Uri.TryCreate(query["url"], UriKind.Absolute, out var target) || target.Scheme != Uri.UriSchemeHttp) return null;
        return new InsecureChoice(target.AbsoluteUri, target.Host, query["always"] == "1");
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

    public static int Code(string address)
        => IsErrorPage(address) && Uri.TryCreate(address, UriKind.Absolute, out var uri)
           && int.TryParse(HttpUtility.ParseQueryString(uri.Query)["code"], out var code)
            ? code
            : 0;

    public static string Render(int code, string failedUrl, bool insecure = false, bool canRemember = true)
    {
        if (insecure) return RenderInsecure(code, failedUrl, canRemember);
        var description = Describe(code);
        var retry = failedUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    || failedUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        var data = new
        {
            title = description.Title,
            detail = description.Detail,
            glyph = description.Glyph,
            url = failedUrl,
            retry,
            offline = code == -106,
            code,
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
            proceedOnce = ContinueUrl(httpUrl, always: false),
            proceedAlways = canRemember ? ContinueUrl(httpUrl, always: true) : null,
        };
        return Template.Value.Replace("{{DATA}}", JsonSerializer.Serialize(data));
    }
}
