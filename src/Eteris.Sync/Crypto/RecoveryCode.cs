using System.Security.Cryptography;
using System.Text;

namespace Eteris.Sync.Crypto;

// Kit de recuperação: o segredo de conta R (256 bits) escrito de uma de duas maneiras; a Google nunca o vê.
//  - 24 palavras da lista BIP39 em português (Bip39Words): 256 bits de R + 8 bits de soma de controlo (o 1.º byte de
//    SHA-256(R)) = 24 × 11 bits, do bit mais significativo para o menos. É o que os kits novos levam. Lê-se com
//    tolerância: maiúsculas, espaços, quebras de linha, vírgulas, numeração e acentos; cada palavra pode ser dada pelas
//    4 primeiras letras. Só R entra na codificação (nada de «seed» BIP39 nem PBKDF2).
//  - O código antigo (até à 0.2.14): 13 grupos de 4 carateres base32 Crockford mais um grupo de controlo (20 bits de
//    SHA-256), «HSJ0-FYH7-…-MCNB». Continua a ser aceite e é o que o perfil exportado e o emparelhamento usam. Lê-se com
//    tolerância: minúsculas, espaços, hífenes, O→0 e I/L→1. Uma gralha é recusada pelo grupo de controlo (passa uma em
//    ~1 milhão) e, quando um caráter não é base32, o erro diz em que grupo está.
// Decode lê os dois formatos. As mensagens de erro nunca levam R nem o conjunto das palavras: no máximo a posição e as
// letras a–z (12 no máximo) de uma ficha que não está na lista.
public static class RecoveryCode
{
    public const int SecretBytes = 32;
    public const int DataChars = 52;   // 256 bits a 5 bits por caráter (os 4 bits que sobram ficam a zero)
    public const int CheckChars = 4;
    public const int Groups = (DataChars + CheckChars) / 4;
    public const int WordCount = 24;
    public const string KitAttribute = "data-eteris-kit";
    public const int MaxKitChars = 1 << 20;
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int WordBits = 11;
    private const int MaxAttributeChars = 1024;
    private const int MaxShownLetters = 12;

    public static string Encode(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != SecretBytes) throw new ArgumentException("O segredo tem de ter 32 bytes", nameof(secret));
        var body = ToBase32(secret) + Check(secret);
        return string.Join('-', Enumerable.Range(0, body.Length / 4).Select(i => body.Substring(i * 4, 4)));
    }

    // 24 palavras em minúsculas separadas por um espaço.
    public static string EncodeWords(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != SecretBytes) throw new ArgumentException("O segredo tem de ter 32 bytes", nameof(secret));
        var data = new byte[SecretBytes + 1];
        var hash = SHA256.HashData(secret);
        try
        {
            secret.CopyTo(data);
            data[SecretBytes] = hash[0];
            var words = new string[WordCount];
            for (var w = 0; w < WordCount; w++)
            {
                var index = 0;
                for (var b = 0; b < WordBits; b++)
                {
                    var bit = w * WordBits + b;
                    index = (index << 1) | ((data[bit / 8] >> (7 - bit % 8)) & 1);
                }
                words[w] = Bip39Words.At(index);
            }
            return string.Join(' ', words);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    // Os dois formatos, por esta ordem: (1) 56 carateres normalizados que são um código antigo válido; (2) se mais de
    // metade das fichas são palavras da lista (ou prefixos que a lista resolve), é um kit em palavras e os erros são os
    // das palavras; (3) senão, os erros do código antigo. Um código antigo quase nunca tem um grupo que seja palavra
    // (os grupos têm algarismos e o alfabeto não tem I, L, O, U); um kit com uma ou duas gralhas passa sempre de metade.
    public static byte[] Decode(string code)
    {
        var s = Normalize(code);
        FormatException? old = null;
        if (s.Length == DataChars + CheckChars)
        {
            try { return DecodeCode(s); }
            catch (FormatException e) { old = e; }
        }
        var tokens = Tokens(code);
        if (tokens.Count > 0 && tokens.Count(t => Bip39Words.Resolve(t) >= 0) * 2 > tokens.Count) return DecodeWords(tokens);
        throw old ?? FailCode(s);
    }

    // O caminho do código antigo: o texto já normalizado; as verificações e as mensagens de sempre, pela mesma ordem.
    private static byte[] DecodeCode(string s)
    {
        if (s.Length != DataChars + CheckChars)
            throw new FormatException($"O código tem {Groups} grupos de 4 carateres; este tem {s.Length} carateres em vez de {DataChars + CheckChars}.");
        for (var i = 0; i < s.Length; i++)
            if (Alphabet.IndexOf(s[i]) < 0) throw new FormatException($"Caráter inválido «{s[i]}» no grupo {i / 4 + 1}.");
        // Os 4 bits que sobram do último caráter de dados são sempre zero: outro valor é uma gralha.
        if ((Alphabet.IndexOf(s[DataChars - 1]) & 0b1111) != 0) throw new FormatException("O código não confere (há uma gralha): verifique os grupos.");
        var secret = FromBase32(s[..DataChars], SecretBytes);
        if (Check(secret) != s[DataChars..])
        {
            CryptographicOperations.ZeroMemory(secret);
            throw new FormatException("O código não confere (há uma gralha): verifique os grupos.");
        }
        return secret;
    }

    // O erro do caminho antigo quando o passo (1) não correu (o texto não tem 56 carateres): DecodeCode lança-o sempre.
    private static FormatException FailCode(string s)
    {
        try { DecodeCode(s); }
        catch (FormatException e) { return e; }
        throw new InvalidOperationException("Um código com outro comprimento não pode ser aceite.");
    }

    // Fichas do texto: o que está entre carateres que não são letra nem algarismo, já dobradas (minúsculas, sem
    // acentos; dobra-se antes de cortar para um acento escrito à parte não partir a palavra). As fichas só de algarismos
    // são a numeração («1.», «01)», «12:») e saem.
    private static List<string> Tokens(string text)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        foreach (var c in Bip39Words.Fold(text))
        {
            if (char.IsLetterOrDigit(c)) { sb.Append(c); continue; }
            Flush();
        }
        Flush();
        return tokens;

        void Flush()
        {
            if (sb.Length == 0) return;
            var token = sb.ToString();
            sb.Clear();
            if (!token.All(char.IsAsciiDigit)) tokens.Add(token);
        }
    }

    private static byte[] DecodeWords(List<string> tokens)
    {
        if (tokens.Count != WordCount)
            throw new FormatException($"O kit de recuperação tem {WordCount} palavras; aqui estão {tokens.Count}.");
        var data = new byte[SecretBytes + 1];
        byte[]? hash = null;
        try
        {
            for (var w = 0; w < WordCount; w++)
            {
                var index = Bip39Words.Resolve(tokens[w]);
                if (index < 0)
                {
                    var shown = ShownToken(tokens[w]);
                    var named = shown.Length == 0 ? $"A palavra {w + 1}" : $"A palavra {w + 1} («{shown}»)";
                    throw new FormatException(index == Bip39Words.TooShort
                        ? $"{named} está incompleta: escreva pelo menos as 4 primeiras letras."
                        : $"{named} não está na lista: verifique como está escrita.");
                }
                for (var b = 0; b < WordBits; b++)
                {
                    if (((index >> (WordBits - 1 - b)) & 1) == 0) continue;
                    var bit = w * WordBits + b;
                    data[bit / 8] |= (byte)(0x80 >> (bit % 8));
                }
            }
            hash = SHA256.HashData(data.AsSpan(0, SecretBytes));
            if (!CryptographicOperations.FixedTimeEquals(hash.AsSpan(0, 1), data.AsSpan(SecretBytes, 1)))
                throw new FormatException("As palavras não conferem (há uma palavra trocada ou fora de ordem): verifique-as pela ordem do kit.");
            return data[..SecretBytes];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
            if (hash is not null) CryptographicOperations.ZeroMemory(hash);
        }
    }

    // A ficha desconhecida, como aparece na mensagem: só as letras a–z (o que foi colado por engano, por exemplo uma
    // palavra-passe, não se repete por inteiro e os algarismos nunca aparecem), cortada às 12 primeiras com «…».
    private static string ShownToken(string folded)
    {
        var letters = new string(folded.Where(c => c is >= 'a' and <= 'z').ToArray());
        return letters.Length > MaxShownLetters ? letters[..MaxShownLetters] + "…" : letters;
    }

    // Palavras da lista que começam por «prefix» (dobrado: sem acentos, minúsculas), no máximo «max». Vazio se o prefixo
    // (dobrado) for vazio, tiver alguma coisa que não seja letra a–z, ou se max <= 0.
    public static IReadOnlyList<string> Complete(string prefix, int max = 8)
    {
        var folded = Bip39Words.Fold(prefix ?? "");
        if (max <= 0 || folded.Length == 0 || !folded.All(c => c is >= 'a' and <= 'z')) return [];
        return Bip39Words.StartingWith(folded, max);
    }

    // Lê o segredo de um ficheiro de kit: primeiro o atributo data-eteris-kit="…"; se não houver, tenta o texto inteiro
    // com TryDecode. Nunca lança. Texto com mais de MaxKitChars → false. O valor do atributo é só letras, algarismos,
    // espaços e hífenes: não há entidades HTML a desfazer.
    public static bool TryReadKit(string fileText, out byte[] secret)
    {
        secret = [];
        if (string.IsNullOrEmpty(fileText) || fileText.Length > MaxKitChars) return false;
        var marker = KitAttribute + "=\"";
        var start = fileText.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return TryDecode(fileText, out secret);
        start += marker.Length;
        var end = fileText.IndexOf('"', start, Math.Min(MaxAttributeChars + 1, fileText.Length - start));
        return end >= 0 && TryDecode(fileText[start..end], out secret);
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
