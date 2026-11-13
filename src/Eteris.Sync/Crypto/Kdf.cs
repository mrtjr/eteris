using System.Security.Cryptography;
using System.Text;

namespace Eteris.Sync.Crypto;

// HKDF-SHA256 com o sal do formato: todas as chaves da sincronização saem daqui, com um «info» diferente por uso.
public static class Kdf
{
    public const string Salt = "eteris-sync-v1";

    public static byte[] Derive(ReadOnlySpan<byte> key, string info, int length = 32)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, key.ToArray(), length, Encoding.UTF8.GetBytes(Salt), Encoding.UTF8.GetBytes(info));

    public static byte[] Derive(ReadOnlySpan<byte> key, string info, ReadOnlySpan<byte> salt, int length = 32)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, key.ToArray(), length, salt.ToArray(), Encoding.UTF8.GetBytes(info));

    public static string Hex(ReadOnlySpan<byte> data) => Convert.ToHexString(data).ToLowerInvariant();

    public static string Base64Url(ReadOnlySpan<byte> data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string? text)
    {
        if (text is null) throw new FormatException("Base64 em falta.");
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}
