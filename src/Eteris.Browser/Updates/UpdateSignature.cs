using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Eteris.Browser.Updates;

// Assinatura das atualizações (INF-02, CAD-01; docs/atualizacoes.md): o releases.win.json de cada release é assinado pelo
// autor com ECDSA P-256 e SHA-256, fora do GitHub; a assinatura r||s (64 bytes, IEEE P1363) vai em Base64 no ficheiro
// releases.win.json.sig, e as chaves públicas (SubjectPublicKeyInfo DER em Base64, uma por linha) vêm embutidas no
// Éteris (update-keys.txt). Só depende de System.*: a ferramenta build/sign compila este mesmo ficheiro.
public static class UpdateSignature
{
    public const string SignatureSuffix = ".sig";

    private const string KeysResource = "Eteris.Browser.Updates.update-keys.txt";

    // Verdadeiro se a assinatura (Base64) for válida para os bytes com alguma das chaves. Nunca lança: uma assinatura ou
    // uma chave ilegível conta como não válida.
    public static bool Verify(ReadOnlySpan<byte> data, string? signatureBase64, IEnumerable<string> publicKeysBase64)
    {
        if (string.IsNullOrWhiteSpace(signatureBase64)) return false;
        byte[] signature;
        try { signature = Convert.FromBase64String(signatureBase64.Trim()); }
        catch (FormatException) { return false; }
        foreach (var key in publicKeysBase64)
        {
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key), out _);
                if (ecdsa.KeySize != 256) continue;
                if (ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) return true;
            }
            catch (FormatException) { }
            catch (CryptographicException) { }
        }
        return false;
    }

    // As linhas de chave de um update-keys.txt: aparadas, não vazias, sem as de comentário (#).
    public static IReadOnlyList<string> ParseKeys(string text)
    {
        var keys = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0 && !line.StartsWith('#')) keys.Add(line);
        }
        return keys;
    }

    // As chaves públicas embutidas (recurso update-keys.txt), lidas uma vez. Vazia se o recurso não existir nesta montagem
    // (a ferramenta build/sign) ou não se ler: sem chaves, nenhuma atualização é aceite.
    public static IReadOnlyList<string> EmbeddedKeys => s_embedded.Value;

    private static readonly Lazy<IReadOnlyList<string>> s_embedded = new(ReadEmbeddedKeys);

    private static IReadOnlyList<string> ReadEmbeddedKeys()
    {
        try
        {
            using var stream = typeof(UpdateSignature).Assembly.GetManifestResourceStream(KeysResource);
            if (stream is null) return [];
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return ParseKeys(reader.ReadToEnd());
        }
        catch (Exception)
        {
            return [];
        }
    }
}

// Um índice sem assinatura válida, ou um pacote cujo SHA256 não é o do índice assinado.
public sealed class UpdateSignatureException(string message) : Exception(message);
