using System.Security.Cryptography;
using System.Text;

namespace Eteris.Sync.Crypto;

// Código de recuperação: o segredo de conta (256 bits) escrito em 13 grupos de 4 carateres base32 Crockford mais um
// grupo de controlo (20 bits de SHA-256), «HSJ0-FYH7-…-MCNB». Mostrado uma vez; a Google nunca o vê.
// Lê-se com tolerância: minúsculas, espaços, hífenes, O→0 e I/L→1. Uma gralha é recusada pelo grupo de controlo
// (passa uma em ~1 milhão) e, quando um caráter não é base32, o erro diz em que grupo está.
public static class RecoveryCode
{
    public const int SecretBytes = 32;
    public const int DataChars = 52;   // 256 bits a 5 bits por caráter (os 4 bits que sobram ficam a zero)
    public const int CheckChars = 4;
    public const int Groups = (DataChars + CheckChars) / 4;
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Encode(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != SecretBytes) throw new ArgumentException("O segredo tem de ter 32 bytes", nameof(secret));
        var body = ToBase32(secret) + Check(secret);
        return string.Join('-', Enumerable.Range(0, body.Length / 4).Select(i => body.Substring(i * 4, 4)));
    }

    public static byte[] Decode(string code)
    {
        var s = Normalize(code);
        if (s.Length != DataChars + CheckChars)
            throw new FormatException($"O código tem {Groups} grupos de 4 carateres; este tem {s.Length} carateres em vez de {DataChars + CheckChars}.");
        for (var i = 0; i < s.Length; i++)
            if (Alphabet.IndexOf(s[i]) < 0) throw new FormatException($"Caráter inválido «{s[i]}» no grupo {i / 4 + 1}.");
        // Os 4 bits que sobram do último caráter de dados são sempre zero: outro valor é uma gralha.
        if ((Alphabet.IndexOf(s[DataChars - 1]) & 0b1111) != 0) throw new FormatException("O código não confere (há uma gralha): verifique os grupos.");
        var secret = FromBase32(s[..DataChars], SecretBytes);
        if (Check(secret) != s[DataChars..]) throw new FormatException("O código não confere (há uma gralha): verifique os grupos.");
        return secret;
    }

    public static bool TryDecode(string code, out byte[] secret)
    {
        try { secret = Decode(code); return true; }
        catch (FormatException) { secret = []; return false; }
    }

    public static string Normalize(string code)
    {
        var sb = new StringBuilder(code.Length);
        foreach (var raw in code.ToUpperInvariant())
        {
            if (raw is '-' or ' ' or '\t' or '\r' or '\n' or ' ') continue;
            sb.Append(raw switch { 'O' => '0', 'I' or 'L' => '1', _ => raw });
        }
        return sb.ToString();
    }

    private static string Check(ReadOnlySpan<byte> secret)
    {
        var input = new byte[12 + secret.Length];
        Encoding.ASCII.GetBytes("eteris-code:").CopyTo(input, 0);
        secret.CopyTo(input.AsSpan(12));
        return ToBase32(SHA256.HashData(input).AsSpan(0, 3))[..CheckChars];
    }

    internal static string ToBase32(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = ((buffer << 8) | b) & 0xFFFF;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    internal static byte[] FromBase32(string text, int length)
    {
        var result = new byte[length];
        int buffer = 0, bits = 0, index = 0;
        foreach (var c in text)
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0) throw new FormatException($"Caráter inválido «{c}».");
            buffer = ((buffer << 5) | value) & 0xFFFF;
            bits += 5;
            if (bits >= 8)
            {
                if (index < length) result[index++] = (byte)(buffer >> (bits - 8));
                bits -= 8;
            }
        }
        if (index != length) throw new FormatException("Código curto demais.");
        return result;
    }
}
