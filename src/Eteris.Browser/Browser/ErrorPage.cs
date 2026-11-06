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

    public static string Url(int code, string failedUrl) => $"{Prefix}?code={code}&url={Uri.EscapeDataString(failedUrl)}";

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

    public static string Render(int code, string failedUrl)
    {
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
}
