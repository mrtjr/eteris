using System.Text;

namespace Eteris.Browser.Omnibox;

public readonly record struct DisplayUrl(string Host, string Rest);

// O endereço em três partes para a barra: o que vem antes do domínio registável (esbatido), o domínio registável
// (destacado, com a porta) e o resto (caminho, pesquisa, fragmento; esbatido).
public readonly record struct DisplayParts(string Subdomains, string Domain, string Rest);

public static class UrlDisplay
{
    private const string Ellipsis = "…";

    // Num endereço http(s) separa o domínio registável (Public Suffix List) dos subdomínios: «accounts.google.com.» fica
    // esbatido e «conta-verificar.example» destacado. IP, «localhost» e o próprio registável não têm subdomínios.
    public static DisplayParts Parts(string url)
    {
        var display = Split(url);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new DisplayParts(string.Empty, display.Host, display.Rest);

        var colon = display.Host.LastIndexOf(':');
        var port = colon > display.Host.LastIndexOf(']') ? display.Host[colon..] : string.Empty;
        var host = port.Length == 0 ? display.Host : display.Host[..colon];
        var registrable = Shields.PublicSuffix.RegistrableDomain(host);
        if (registrable.Length > 0 && host.EndsWith("." + registrable, StringComparison.OrdinalIgnoreCase))
            return new DisplayParts(host[..(host.Length - registrable.Length)], host[^registrable.Length..] + port, display.Rest);
        return new DisplayParts(string.Empty, display.Host, display.Rest);
    }

    // Cabe em «available» (na unidade de «measure») sem esconder o domínio: se subdomínios e domínio não cabem, tira
    // etiquetas à esquerda dos subdomínios («…com.»); se nem o domínio cabe, corta-o pela esquerda (fica o fim, o
    // sufixo) e o caminho sai. O caminho nunca entra na conta: quem o corta, no fim, é o TextTrimming da barra.
    public static DisplayParts Fit(DisplayParts parts, double available, Func<string, double> measure)
    {
        if (measure(parts.Subdomains + parts.Domain) <= available) return parts;

        var rest = parts.Subdomains;
        while (rest.Length > 0)
        {
            var dot = rest.IndexOf('.');
            rest = dot < 0 ? string.Empty : rest[(dot + 1)..];
            if (measure(Ellipsis + rest + parts.Domain) <= available) return new DisplayParts(Ellipsis + rest, parts.Domain, parts.Rest);
        }

        var tail = parts.Domain;
        while (tail.Length > 1 && measure(Ellipsis + tail) > available) tail = tail[1..];
        return new DisplayParts(Ellipsis, tail, string.Empty);
    }

    public static DisplayUrl Split(string url)
    {
        // Um data: ou javascript: é conteúdo, não endereço: mostra só o tipo, nunca o que lá vem (pode imitar um site).
        if (url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return new DisplayUrl(string.Empty, "javascript:…");
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return new DisplayUrl(string.Empty, "data:" + DataType(url) + "…");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new DisplayUrl(url, string.Empty);

        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        if (!uri.IsDefaultPort) host += ":" + uri.Port;

        var afterScheme = url.IndexOf("://", StringComparison.Ordinal) + 3;
        var restStart = url.IndexOfAny(['/', '?', '#'], afterScheme);
        var rest = restStart < 0 ? string.Empty : url[restStart..];
        if (rest == "/") rest = string.Empty;
        return new DisplayUrl(host, DecodeForDisplay(rest));
    }

    // O texto até ao primeiro «;» ou «,», se for um tipo MIME curto e simples; senão, nada.
    private static string DataType(string url)
    {
        var rest = url["data:".Length..];
        var end = rest.IndexOfAny([';', ',']);
        var type = (end < 0 ? rest : rest[..end]).ToLowerInvariant();
        return type.Length <= 40 && type.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '/' or '+' or '.' or '-') ? type : string.Empty;
    }

    public static bool IsSecure(string url)
        => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("eteris://", StringComparison.OrdinalIgnoreCase);

    // Descodifica %XX para leitura humana, mas mantém codificados espaços e caracteres de controlo
    // (um URL não pode parecer diferente do que realmente é).
    private static string DecodeForDisplay(string text)
    {
        var output = new StringBuilder(text.Length);
        var bytes = new List<byte>();
        var i = 0;
        while (i < text.Length)
        {
            if (IsEscape(text, i))
            {
                var start = i;
                bytes.Clear();
                while (IsEscape(text, i))
                {
                    bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                    i += 3;
                }
                AppendDecoded(output, bytes, text[start..i]);
                continue;
            }
            output.Append(text[i]);
            i++;
        }
        return output.ToString();
    }

    private static void AppendDecoded(StringBuilder output, List<byte> bytes, string original)
    {
        string decoded;
        try
        {
            decoded = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes.ToArray());
        }
        catch (DecoderFallbackException)
        {
            output.Append(original);
            return;
        }

        foreach (var c in decoded)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c) || c is '%' or '/' or '?' or '#')
                output.Append(Uri.EscapeDataString(c.ToString()));
            else
                output.Append(c);
        }
    }

    private static bool IsEscape(string text, int i)
        => i + 2 < text.Length && text[i] == '%' && char.IsAsciiHexDigit(text[i + 1]) && char.IsAsciiHexDigit(text[i + 2]);
}
