using System.Buffers.Binary;

namespace Eteris.Browser.Browser;

public enum SniffedImage { Png, Jpeg, Gif, Ico }

/// <summary>
/// Olha só para os cabeçalhos dos bytes de imagem que vêm de sites (ícones, imagem do Espreitar) antes de eles chegarem
/// ao descodificador do Windows, que corre no processo principal: só PNG, JPEG, GIF e ICO, reconhecidos pela assinatura
/// (nunca pelo tipo que o servidor diz), e com as dimensões declaradas dentro de um limite. Não descodifica nada.
/// </summary>
public static class ImageSniff
{
    public const int MaxIconSide = 512;
    public const int MaxPreviewSide = 4096;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static SniffedImage? Kind(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(PngSignature)) return SniffedImage.Png;
        if (bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8)) return SniffedImage.Gif;
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return SniffedImage.Jpeg;
        if (bytes.Length >= 6 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 1 && bytes[3] == 0)
        {
            var entries = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
            if (entries is >= 1 and <= 64) return SniffedImage.Ico;
        }
        return null;
    }

    /// <summary>Dimensões lidas do cabeçalho, ou null se o tipo não é aceite ou o cabeçalho não se deixa ler.</summary>
    public static (int Width, int Height)? Size(ReadOnlySpan<byte> bytes) => Kind(bytes) switch
    {
        SniffedImage.Png => PngSize(bytes),
        SniffedImage.Gif => bytes.Length >= 10
            ? (BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]))
            : null,
        SniffedImage.Jpeg => JpegSize(bytes),
        SniffedImage.Ico => IcoSize(bytes),
        _ => null,
    };

    public static bool Accepts(ReadOnlySpan<byte> bytes, int maxSide)
        => Size(bytes) is { } size && size.Width >= 1 && size.Height >= 1 && size.Width <= maxSide && size.Height <= maxSide;

    private static (int, int)? PngSize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 24 || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8)) return null;
        var width = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]);
        return (Clamp(width), Clamp(height));
    }

    // Valores acima de int.MaxValue ficam em int.MaxValue: já nenhum limite os aceita.
    private static int Clamp(uint value) => value > int.MaxValue ? int.MaxValue : (int)value;

    private static (int, int)? JpegSize(ReadOnlySpan<byte> bytes)
    {
        var p = 2;
        while (p + 1 < bytes.Length)
        {
            if (bytes[p] != 0xFF) return null;
            var marker = bytes[p + 1];
            if (marker == 0xFF) { p++; continue; }                       // bytes de enchimento
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) { p += 2; continue; } // sem comprimento
            if (marker is 0xD9 or 0xDA or 0x00) return null;             // fim ou dados da imagem sem quadro
            if (p + 4 > bytes.Length) return null;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes[(p + 2)..]);
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                if (p + 9 > bytes.Length) return null;
                return (BinaryPrimitives.ReadUInt16BigEndian(bytes[(p + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(bytes[(p + 5)..]));
            }
            if (length < 2) return null;
            p += 2 + length;
        }
        return null;
    }

    private static (int, int)? IcoSize(ReadOnlySpan<byte> bytes)
    {
        var entries = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        var width = 0;
        var height = 0;
        for (var i = 0; i < entries; i++)
        {
            var at = 6 + i * 16;
            if (at + 16 > bytes.Length) return null;
            var entry = bytes.Slice(at, 16);
            var w = entry[0] == 0 ? 256 : entry[0];
            var h = entry[1] == 0 ? 256 : entry[1];

            // O que a entrada declara não manda: o conteúdo traz o seu próprio cabeçalho, e é esse que o descodificador segue.
            var size = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            if ((long)offset + size > bytes.Length) return null;
            var content = bytes.Slice((int)offset, (int)size);
            if (content.StartsWith(PngSignature))
            {
                if (PngSize(content) is not { } png) return null;
                w = Math.Max(w, png.Item1);
                h = Math.Max(h, png.Item2);
            }
            else if (content.Length >= 12)
            {
                var dibWidth = BinaryPrimitives.ReadInt32LittleEndian(content[4..]);
                var dibHeight = BinaryPrimitives.ReadInt32LittleEndian(content[8..]);
                if (dibWidth < 0 || dibHeight < 0) return null;
                w = Math.Max(w, dibWidth);
                h = Math.Max(h, dibHeight / 2);
            }
            width = Math.Max(width, w);
            height = Math.Max(height, h);
        }
        return (width, height);
    }
}
