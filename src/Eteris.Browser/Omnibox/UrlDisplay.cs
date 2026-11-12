using System.Text;

namespace Eteris.Browser.Omnibox;

public readonly record struct DisplayUrl(string Host, string Rest);

public static class UrlDisplay
{
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
