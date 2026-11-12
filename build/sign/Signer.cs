using System.Security.Cryptography;

namespace Eteris.Sign;

// A chave das atualizações do Éteris (docs/atualizacoes.md): ECDSA P-256, a privada em PKCS#8 cifrado (PEM, AES-256-CBC
// com PBKDF2-SHA256), a pública em SubjectPublicKeyInfo (DER) em Base64 e a assinatura r||s (IEEE P1363) de 64 bytes em
// Base64, com SHA-256. O Éteris verifica com Eteris.Browser.Updates.UpdateSignature.Verify.
public static class Signer
{
    // Iterações do PBKDF2 que protege a chave privada com a frase-passe.
    public const int Iterations = 600_000;

    public static string NewKeyPem(string password)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return key.ExportEncryptedPkcs8PrivateKeyPem(password,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, Iterations));
    }

    // A linha a pôr em src/Eteris.Browser/Updates/update-keys.txt. Uma frase-passe errada lança CryptographicException.
    public static string PublicKey(string pem, string password)
    {
        using var key = Open(pem, password);
        return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    // A assinatura dos bytes, em Base64 (o conteúdo do ficheiro .sig).
    public static string Sign(string pem, string password, byte[] data)
    {
        using var key = Open(pem, password);
        return Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    private static ECDsa Open(string pem, string password)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportFromEncryptedPem(pem, password);
            if (key.KeySize != 256) throw new CryptographicException("a chave não é P-256");
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }
}
