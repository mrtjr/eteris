using System.IO;
using System.Security.Cryptography;
using Eteris.Sync.Google;

namespace Eteris.Browser.Vault;

// O cofre não abre: a chave não sai da DPAPI (outro utilizador do Windows, outra entropia, ficheiro estragado) ou
// desapareceu com o cofre ainda no disco. Nada é substituído nem apagado.
public sealed class VaultUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

// A chave do cofre (docs/palavras-passe.md, A2): 256 bits aleatórios, criados uma vez, guardados em <raiz>\cofre.key
// embrulhados pela DPAPI do utilizador (CryptProtectData sem LOCAL_MACHINE, sem interface, entropia
// «Eteris.Sync:cofre», Sync/Dpapi.cs). Quem chama apaga a cópia devolvida (CryptographicOperations.ZeroMemory).
public static class VaultKey
{
    public const string FileName = "cofre.key";
    public const string Purpose = "cofre";
    public const int Length = 32;

    public static byte[] LoadOrCreate(string path, ISecretProtector protector)
    {
        if (File.Exists(path)) return Load(path, protector);
        var key = RandomNumberGenerator.GetBytes(Length);
        var temp = path + ".novo";
        try
        {
            // Descarregada para o disco antes de ganhar o nome: um corte de energia deixava-a vazia, com o cofre cifrado com ela.
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var wrapped = protector.Protect(key);
                stream.Write(wrapped, 0, wrapped.Length);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: false);
            return key;
        }
        catch (IOException) when (File.Exists(path))
        {
            // Outra janela criou-a ao mesmo tempo: fica a dela.
            CryptographicOperations.ZeroMemory(key);
            try { File.Delete(temp); } catch (IOException) { }
            return Load(path, protector);
        }
    }

    private static byte[] Load(string path, ISecretProtector protector)
    {
        byte[] key;
        try { key = protector.Unprotect(File.ReadAllBytes(path)); }
        catch (CryptographicException e) { throw new VaultUnavailableException("A chave do cofre não abre nesta conta do Windows.", e); }
        if (key.Length == Length) return key;
        CryptographicOperations.ZeroMemory(key);
        throw new VaultUnavailableException("A chave do cofre está estragada.");
    }
}
