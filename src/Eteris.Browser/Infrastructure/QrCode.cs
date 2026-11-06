using QRCoder;

namespace Eteris.Browser.Infrastructure;

// Código QR de um endereço, gerado neste computador (nenhum serviço externo vê o endereço).
public static class QrCode
{
    public static byte[] Png(string text, int pixelsPerModule = 12)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule, [0x0B, 0x0D, 0x13, 0xFF], [0xFF, 0xFF, 0xFF, 0xFF]);
    }

    public static string FileName(string url)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "pagina";
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
        return $"QR {host}.png";
    }

    // O endereço legível debaixo do código: sem esquema e, se for longo, encurtado ao meio.
    public static string Caption(string url, int max = 46)
    {
        var text = url.Replace("https://", "").Replace("http://", "").TrimEnd('/');
        return text.Length <= max ? text : text[..(max / 2)] + "…" + text[^(max / 2 - 1)..];
    }
}
