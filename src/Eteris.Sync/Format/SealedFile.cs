using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Eteris.Sync.Crypto;

namespace Eteris.Sync.Format;

// Ficheiro cifrado do Éteris (lotes, instantâneos, estado dos aparelhos, perfil exportado, emparelhamento):
//
//   "ETS" | tipo (1) | versão (1) | geração (u32) | opções (1) | n (1) | público (n) | nonce (12) | cifra | etiqueta (16)
//
// Em claro só o cabeçalho (nada do utilizador) e o «público» (o sal de um perfil exportado). O conteúdo é JSON,
// comprimido com Brotli (salvo quando leva abas abertas, ver em baixo) e PREENCHIDO: até ao múltiplo seguinte de
// 1 KiB e, nos ficheiros grandes, até ao «balde» Padmé do tamanho (só os bits de cima ficam; no máximo ~12 % a mais).
// O tamanho dá a ordem de grandeza (um instantâneo tem o estado inteiro, cresce com o perfil), não o número exato de
// favoritos nem o tamanho de uma edição. Cifrado com AES-256-GCM. Os dados associados (AAD) juntam o cabeçalho, a
// cadeia e o «contexto» — o nome lógico do ficheiro (aparelho e número do lote) —: trocar, reordenar ou mudar de nome
// um ficheiro falha a verificação.
//
// Sem compressão quando o ficheiro leva a coleção «tabs»: os títulos das abas são escolhidos pelos sites e mudam a
// cada envio, e comprimi-los juntos com URLs que podem ter segredos (tokens, ligações de redefinição de senha) abre
// um canal lateral de compressão (tipo CRIME/BREACH) a quem vê os tamanhos. A opção vai no cabeçalho (autenticado).
public static class SealedFile
{
    public const int FormatVersion = 1;
    public const int PadBlock = 1024;
    private const int FixedHeader = 10;
    private const byte OptionBrotli = 1;
    public const int MaxPlainBytes = 64 * 1024 * 1024;
    // O maior ficheiro selado que um transporte entrega (lote ou instantâneo): o conteúdo máximo, com o enchimento, a
    // etiqueta e o cabeçalho, e uma folga. Acima disto nem se lê (SyncPaths.MaxBytes).
    public const int MaxFileBytes = 72 * 1024 * 1024;

    public const char Batch = 'B';
    public const char Snapshot = 'S';
    public const char DeviceState = 'A';
    public const char Archive = 'P';
    public const char Pairing = 'E';
    public const char Rekey = 'R';

    public sealed record Header(char Kind, int Version, int Generation, byte Options, byte[] Public, int Length);

    public static byte[] Seal(ReadOnlySpan<byte> key, char kind, string chain, int generation, string context, ReadOnlySpan<byte> json,
        ReadOnlySpan<byte> publicPart = default, int version = FormatVersion, bool compress = true)
    {
        if (publicPart.Length > 255) throw new ArgumentException("Parte pública grande demais", nameof(publicPart));
        var header = new byte[FixedHeader + 1 + publicPart.Length];
        "ETS"u8.CopyTo(header);
        header[3] = (byte)kind;
        header[4] = (byte)version;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(5), generation);
        header[9] = compress ? OptionBrotli : (byte)0;
        header[10] = (byte)publicPart.Length;
        publicPart.CopyTo(header.AsSpan(11));

        var compressed = compress ? Compress(json) : json.ToArray();
        var plainLength = PaddedLength(4 + compressed.Length);
        var plain = new byte[plainLength]; // o resto fica a zeros: é o preenchimento
        BinaryPrimitives.WriteInt32BigEndian(plain, compressed.Length);
        compressed.CopyTo(plain, 4);

        var output = new byte[header.Length + 12 + plain.Length + 16];
        header.CopyTo(output, 0);
        var nonce = output.AsSpan(header.Length, 12);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plain, output.AsSpan(header.Length + 12, plain.Length), output.AsSpan(output.Length - 16),
            Aad(header, chain, context));
        CryptographicOperations.ZeroMemory(plain);
        return output;
    }

    // Múltiplo de 1 KiB e, acima disso, Padmé (Nikitin et al., 2019): com o tamanho L entre 2^E e 2^(E+1), só os
    // ⌊log2 E⌋+1 bits de cima ficam, o resto arredonda para cima. Fuga de O(log log L) bits, custo ≤ ~12 %.
    internal static int PaddedLength(int length)
    {
        var padded = Padme(length);
        padded += (PadBlock - padded % PadBlock) % PadBlock;
        return checked((int)padded);
    }

    private static long Padme(long length)
    {
        if (length < 2) return length;
        var e = 63 - BitOperations.LeadingZeroCount((ulong)length);
        var s = 64 - BitOperations.LeadingZeroCount((ulong)e);
        var mask = (1L << (e - s)) - 1;
        return (length + mask) & ~mask;
    }

    public static Header ReadHeader(ReadOnlySpan<byte> file)
    {
        if (file.Length < FixedHeader + 1 + 12 + 16 || !file[..3].SequenceEqual("ETS"u8)) throw new FormatException("Não é um ficheiro do Éteris.");
        var publicLength = file[10];
        if (file.Length < FixedHeader + 1 + publicLength + 12 + 16) throw new FormatException("Ficheiro cortado.");
        return new Header((char)file[3], file[4], BinaryPrimitives.ReadInt32BigEndian(file[5..]), file[9],
            file.Slice(11, publicLength).ToArray(), FixedHeader + 1 + publicLength);
    }

    // Devolve o JSON; lança UnsupportedFormatException (versão nova), FormatException (não é nosso / tipo errado) ou
    // CryptographicException (chave errada, adulterado ou fora do lugar).
    public static byte[] Open(ReadOnlySpan<byte> key, char kind, string chain, string context, ReadOnlySpan<byte> file)
    {
        var header = ReadHeader(file);
        if (header.Version > FormatVersion)
            throw new UnsupportedFormatException($"Ficheiro no formato {header.Version} (este Éteris lê até ao {FormatVersion}): atualize o Éteris.");
        if (header.Kind != kind) throw new FormatException($"Esperava um ficheiro do tipo {kind} e veio {header.Kind}.");
        var cipherLength = file.Length - header.Length - 12 - 16;
        var plain = new byte[cipherLength];
        using (var aes = new AesGcm(key, 16))
            aes.Decrypt(file.Slice(header.Length, 12), file.Slice(header.Length + 12, cipherLength), file[^16..], plain,
                Aad(file[..header.Length], chain, context));
        // Selado com a chave certa mas sem sítio para o comprimento (quem tem a chave também pode escrever lixo).
        if (plain.Length < 4) throw new FormatException("Conteúdo cortado.");
        var length = BinaryPrimitives.ReadInt32BigEndian(plain);
        if (length < 0 || length > plain.Length - 4) throw new FormatException("Comprimento interno inválido.");
        var body = plain.AsSpan(4, length);
        return (header.Options & OptionBrotli) != 0 ? Decompress(body) : body.ToArray();
    }

    private static byte[] Aad(ReadOnlySpan<byte> header, string chain, string context)
    {
        var tail = Encoding.UTF8.GetBytes("|" + chain + "|" + context);
        var aad = new byte[header.Length + tail.Length];
        header.CopyTo(aad);
        tail.CopyTo(aad, header.Length);
        return aad;
    }

    private static byte[] Compress(ReadOnlySpan<byte> data)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true)) brotli.Write(data);
        return output.ToArray();
    }

    private static byte[] Decompress(ReadOnlySpan<byte> data)
    {
        using var input = new MemoryStream(data.ToArray());
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        try
        {
            int read;
            while ((read = brotli.Read(buffer)) > 0)
            {
                // Um lote verdadeiro tem poucos MB: mais do que isto é um ficheiro feito para esgotar a memória.
                if (output.Length + read > MaxPlainBytes) throw new FormatException("Conteúdo grande demais.");
                output.Write(buffer, 0, read);
            }
        }
        // O descompressor recusa o conteúdo: é um ficheiro que não se lê, como os outros (quem lê trata FormatException).
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException)
        {
            throw new FormatException("Conteúdo comprimido ilegível.", e);
        }
        return output.ToArray();
    }

    internal static string Hex(ReadOnlySpan<byte> data) => Kdf.Hex(data);
}
