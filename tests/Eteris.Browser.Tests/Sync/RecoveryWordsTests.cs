using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Eteris.Sync.Crypto;

namespace Eteris.Browser.Tests.Sync;

// O kit de recuperação em 24 palavras (BIP39, lista portuguesa) e os códigos antigos de 56 carateres, que continuam a
// ser aceites. Tudo lógica pura: os segredos são aleatórios ou os vetores públicos do BIP39, nunca um código real.
public class RecoveryWordsTests
{
    // Vetor «e»: os bytes 00 01 02 … 1f. É o segredo fixo dos testes de gralhas (nenhuma troca destes testes passa por
    // acaso na soma de controlo).
    private const string PhraseE =
        "abacate afivelar inativo afivelar donzela achatar barulho beber albergue exalar cuidado baioneta abotoar joia dever informal argola peneira aditivo celebrar ouvido amarelo gritaria toalha";

    private static byte[] EntropyE => Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    private static string[] WordsE => PhraseE.Split(' ');

    private static string WithWord(int position, string word)
    {
        var words = WordsE;
        words[position - 1] = word;
        return string.Join(' ', words);
    }

    private static string Thrice(string eight, string last)
    {
        var words = Enumerable.Repeat(eight.Split(' '), 3).SelectMany(w => w).ToArray();
        words[23] = last;
        return string.Join(' ', words);
    }

    private static int[] ThriceIndices(int[] eight, int last)
    {
        var indices = Enumerable.Repeat(eight, 3).SelectMany(i => i).ToArray();
        indices[23] = last;
        return indices;
    }

    public static TheoryData<string, int[], string> Vectors => new()
    {
        { new string('0', 64), [.. Enumerable.Repeat(0, 23), 102], string.Join(' ', Enumerable.Repeat("abacate", 23).Append("alinhar")) },
        { string.Concat(Enumerable.Repeat("7f", 32)), ThriceIndices([1019, 2015, 1790, 2039, 1983, 1533, 2031, 1919], 1815),
            Thrice("imitador vinheta sogro xerife veleiro pomar volumoso tratador", "sucata") },
        { string.Concat(Enumerable.Repeat("80", 32)), ThriceIndices([1028, 32, 257, 8, 64, 514, 16, 128], 189),
            Thrice("inalador acirrar barulho abotoar afivelar coruja abutre amostra", "asilado") },
        { new string('f', 64), [.. Enumerable.Repeat(2047, 23), 1967], string.Join(' ', Enumerable.Repeat("zumbido", 23).Append("validade")) },
        { Convert.ToHexString(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()),
            [0, 64, 1030, 64, 643, 28, 257, 266, 88, 771, 540, 241, 8, 1096, 610, 1045, 176, 1478, 50, 417, 1422, 116, 963, 1891], PhraseE },
        { "68a79eaca2324873eacc50cb9c6eca8cc68ea5d936f98787c60c7ebc74e6ce7c",
            [837, 487, 1369, 547, 292, 463, 1369, 1104, 1628, 1819, 1429, 204, 839, 663, 806, 1785, 1084, 497, 1048, 2027, 1594, 923, 463, 1024],
            "filho comentar obrigado cunhado biologia clone obrigado julgador rebolar sufixo pacote atingir filtrar edital fardado siri janta conhecer inibido vogal quiosque genoma clone impor" },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Words_MatchTheBip39Vectors(string entropyHex, int[] indices, string phrase)
    {
        var entropy = Convert.FromHexString(entropyHex);
        var words = RecoveryCode.EncodeWords(entropy);
        Assert.Equal(phrase, words);
        Assert.Equal(indices.Select(Bip39Words.At), words.Split(' '));
        Assert.Equal(entropy, RecoveryCode.Decode(phrase));
    }

    [Fact]
    public void Words_RoundTrip_AndAre24LowercaseWordsFromTheList()
    {
        var list = Bip39Words.All.ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < 50; i++)
        {
            var secret = RandomNumberGenerator.GetBytes(32);
            var words = RecoveryCode.EncodeWords(secret);
            var tokens = words.Split(' ');
            Assert.Equal(RecoveryCode.WordCount, tokens.Length);
            Assert.All(tokens, t => Assert.Contains(t, list));
            Assert.Matches("^[a-z]+( [a-z]+){23}$", words);
            Assert.Equal(secret, RecoveryCode.Decode(words));
        }
    }

    [Fact]
    public void Words_AreReadWithTolerance()
    {
        var words = WordsE;
        string[] typed =
        [
            PhraseE.ToUpperInvariant(),
            string.Join("  \t ", words),
            string.Join("\r\n", words),
            string.Join("\n", words.Select((w, i) => $"{i + 1}. {w}")),
            string.Join(" ", words.Select((w, i) => $"{i + 1:00}) {w}")),
            string.Join(", ", words),
            " " + PhraseE + "\n",
        ];
        foreach (var text in typed) Assert.Equal(EntropyE, RecoveryCode.Decode(text));
    }

    [Fact]
    public void Words_AcceptFourLetterPrefixes()
    {
        const string four = "abac afiv inat afiv donz acha baru bebe albe exal cuid baio abot joia deve info argo pene adit cele ouvi amar grit toal";
        Assert.Equal(EntropyE, RecoveryCode.Decode(four));
        Assert.Equal(EntropyE, RecoveryCode.Decode("abacat" + four[4..]));
    }

    [Fact]
    public void Words_ShortPrefix_IsRefused()
    {
        // «don» começa «donativo» e «donzela»: com menos de 4 letras nunca se adivinha.
        var e = Assert.Throws<FormatException>(() => RecoveryCode.Decode(WithWord(5, "don")));
        Assert.Contains("palavra 5", e.Message);
        Assert.Contains("don", e.Message);
        Assert.Contains("4 primeiras letras", e.Message);
    }

    [Fact]
    public void Words_AcceptAccentsTheUserTypes()
    {
        var words = WordsE;
        words[0] = "abácate";
        words[1] = "AFIVELAR";
        words[2] = "inatívo";
        Assert.Equal(EntropyE, RecoveryCode.Decode(string.Join(' ', words)));
        // O mesmo acento escrito em duas partes (letra + acento combinado) também se lê.
        words[0] = "abácate";
        Assert.Equal(EntropyE, RecoveryCode.Decode(string.Join(' ', words)));
    }

    [Fact]
    public void Words_RejectEverySingleWordSwap()
    {
        var accepted = 0;
        for (var i = 0; i < 24; i++)
        {
            var words = WordsE;
            words[i] = Bip39Words.At((Bip39Words.IndexOf(words[i]) + 7) % Bip39Words.Count);
            if (RecoveryCode.TryDecode(string.Join(' ', words), out _)) accepted++;
        }
        Assert.Equal(0, accepted);
    }

    [Fact]
    public void Words_RejectSwappedOrder_WithoutEchoingTheKit()
    {
        var words = WordsE;
        (words[1], words[2]) = (words[2], words[1]);
        var text = string.Join(' ', words);
        Assert.False(RecoveryCode.TryDecode(text, out _));
        var e = Assert.Throws<FormatException>(() => RecoveryCode.Decode(text));
        Assert.Contains("não conferem", e.Message);
        Assert.DoesNotContain("afivelar", e.Message);
        Assert.DoesNotContain("inativo", e.Message);
    }

    [Fact]
    public void Words_WrongCount_SaysHowMany()
    {
        var fewer = Assert.Throws<FormatException>(() => RecoveryCode.Decode(string.Join(' ', WordsE[..23])));
        Assert.Contains("24 palavras", fewer.Message);
        Assert.Contains("23", fewer.Message);
        var more = Assert.Throws<FormatException>(() => RecoveryCode.Decode(PhraseE + " abacate"));
        Assert.Contains("24 palavras", more.Message);
        Assert.Contains("25", more.Message);
    }

    [Fact]
    public void Words_UnknownWord_SaysWhichPosition_AndCutsIt()
    {
        static string Message(string word) => Assert.Throws<FormatException>(() => RecoveryCode.Decode(WithWord(5, word))).Message;
        static string Shown(string message) => Regex.Match(message, "«(.*)»").Groups[1].Value;
        static string WithoutPosition(string message) => message.Replace("palavra 5", "");

        var plain = Message("zzzzzz");
        Assert.Contains("palavra 5", plain);
        Assert.Contains("zzzzzz", plain);

        var long200 = Message(new string('z', 200));
        Assert.Contains("palavra 5", long200);
        Assert.Contains("zzzzzzzzzzzz…", long200);
        Assert.DoesNotContain(new string('z', 13), long200);

        // O que se colou por engano (uma palavra-passe, por exemplo) não se repete: só as letras a–z, nunca algarismos.
        var digit = Message("abac4te");
        Assert.Contains("palavra 5", digit);
        Assert.Contains("abacte", digit);
        Assert.DoesNotContain("abac4te", digit);
        Assert.DoesNotMatch("[0-9]", WithoutPosition(digit));

        var password = Message("p4ssw0rd2024xyz");
        Assert.Contains("palavra 5", password);
        Assert.Contains("psswrdxyz", password);
        Assert.DoesNotMatch("[0-9]", WithoutPosition(password));
        Assert.Matches("^[a-z]+$", Shown(password));

        // Sem nenhuma letra a–z, a mensagem sai sem a ficha.
        Assert.Equal("A palavra 5 não está na lista: verifique como está escrita.", Message("домик"));
    }

    // Achado A31: 14 palavras de 4 letras dão 56 carateres normalizados, mas são um kit, não um código antigo.
    [Fact]
    public void FourteenFourLetterWords_GetTheWordsError()
    {
        var e = Assert.Throws<FormatException>(() => RecoveryCode.Decode("alvo anel anjo arma ateu atum aura bico bolo breu broa bule cabo caju"));
        Assert.Contains("24 palavras", e.Message);
        Assert.Contains("14", e.Message);
        Assert.DoesNotContain("grupo", e.Message);
        Assert.Contains("aqui estão 1", Assert.Throws<FormatException>(() => RecoveryCode.Decode("abacate")).Message);
    }

    [Fact]
    public void OldCodes_AreStillAccepted()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var code = RecoveryCode.Encode(bytes);
        Assert.Equal(bytes, RecoveryCode.Decode(code));
        Assert.Equal(bytes, RecoveryCode.Decode(code.ToLowerInvariant().Replace('-', ' ')));
        using var secret = AccountSecret.FromBytes(bytes);
        using var fromCode = AccountSecret.FromCode(secret.ToCode());
        using var fromWords = AccountSecret.FromCode(secret.ToWords());
        Assert.Equal(secret.ChainId, fromCode.ChainId);
        Assert.Equal(secret.ChainId, fromWords.ChainId);
    }

    [Fact]
    public void OldCode_ErrorsAreUnchanged()
    {
        var plain = RecoveryCode.Normalize(RecoveryCode.Encode(EntropyE));
        var shortCode = Assert.Throws<FormatException>(() => RecoveryCode.Decode(plain[..52]));
        Assert.Contains("14 grupos de 4", shortCode.Message);
        Assert.Contains("em vez de 56", shortCode.Message);
        var badChar = Assert.Throws<FormatException>(() => RecoveryCode.Decode(plain[..8] + "U" + plain[9..]));
        Assert.Contains("grupo 3", badChar.Message);
        // Um código válido escrito em 14 grupos separados por espaços, com um caráter trocado.
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var typo = plain[..20] + alphabet[(alphabet.IndexOf(plain[20]) + 7) % 32] + plain[21..];
        var grouped = string.Join(' ', Enumerable.Range(0, 14).Select(g => typo.Substring(g * 4, 4)));
        var e = Assert.Throws<FormatException>(() => RecoveryCode.Decode(grouped));
        Assert.True(e.Message.Contains("não confere") || e.Message.Contains("Caráter inválido"), e.Message);
        Assert.DoesNotContain("palavra", e.Message);
    }

    [Fact]
    public void Complete_ListsWordsByPrefix()
    {
        var al = RecoveryCode.Complete("al", 50);
        Assert.Equal(30, al.Count);
        Assert.All(al, w => Assert.StartsWith("al", w, StringComparison.Ordinal));
        Assert.Contains("alicate", al);
        Assert.Equal(8, RecoveryCode.Complete("al").Count);
        Assert.Equal(["alicate"], RecoveryCode.Complete("alic"));
        Assert.Equal(["alicate"], RecoveryCode.Complete("ALÍC"));
        Assert.Empty(RecoveryCode.Complete(""));
        Assert.Empty(RecoveryCode.Complete("a1"));
        Assert.Empty(RecoveryCode.Complete("al", 0));
    }

    [Fact]
    public void TryReadKit_ReadsTheAttribute_AndFallsBackToPlainText()
    {
        var a = RecoveryCode.KitAttribute;
        Assert.True(RecoveryCode.TryReadKit($"<html><body {a}=\"{PhraseE}\"><p>…</p></body></html>", out var fromAttribute));
        Assert.Equal(EntropyE, fromAttribute);
        Assert.True(RecoveryCode.TryReadKit("\n" + PhraseE + "\n", out var fromText));
        Assert.Equal(EntropyE, fromText);
        var bytes = RandomNumberGenerator.GetBytes(32);
        Assert.True(RecoveryCode.TryReadKit($"<html><body {a}=\"{RecoveryCode.Encode(bytes)}\"></body></html>", out var fromOldCode));
        Assert.Equal(bytes, fromOldCode);

        var words = WordsE;
        (words[1], words[2]) = (words[2], words[1]);
        string[] refused =
        [
            "",
            "<html><body><p>Nada aqui.</p></body></html>",
            $"<html><body {a}=\"{string.Join(' ', words)}\"></body></html>",
            $"<html><body {a}=\"{PhraseE}",
            new string('a', RecoveryCode.MaxKitChars + 1),
            // Unicode inválido (um substituto solto) no meio das palavras: recusado, sem exceção.
            PhraseE.Replace("joia", "jo\uD800ia"),
        ];
        foreach (var text in refused) Assert.False(RecoveryCode.TryReadKit(text, out _));
    }

    [Fact]
    public void WordList_IsTheOfficialOne()
    {
        var bytes = Bip39Words.ReadResource();
        Assert.Equal(15_671, bytes.Length);
        Assert.Equal("2685e9c194c82ae67e10ba59d9ea5345a23dc093e92276fc5361f6667d79cd3f", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        Assert.DoesNotContain((byte)0x0D, bytes);
        var all = Bip39Words.All;
        Assert.Equal(2048, all.Count);
        Assert.Equal(2048, all.Distinct(StringComparer.Ordinal).Count());
        Assert.All(all, w => Assert.Matches("^[a-z]{4,8}$", w));
        Assert.Equal(2048, all.Select(w => w[..4]).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("abacate", Bip39Words.At(0));
        Assert.Equal("zumbido", Bip39Words.At(2047));
        Assert.Equal(102, Bip39Words.IndexOf("alinhar"));
        Assert.Equal(0, Bip39Words.Resolve("abac"));
        Assert.Equal(Bip39Words.TooShort, Bip39Words.Resolve("aba"));
        Assert.Equal(Bip39Words.Unknown, Bip39Words.Resolve("zzzz"));
    }

    // Guarda: o emparelhamento usa Normalize, ToBase32 e FromBase32 do código antigo; não podem mudar.
    [Fact]
    public void Pairing_IsUntouched()
    {
        Assert.Equal("00111", RecoveryCode.Normalize("o0-iL 1"));
        var ten = RandomNumberGenerator.GetBytes(10);
        Assert.Equal(ten, RecoveryCode.FromBase32(RecoveryCode.ToBase32(ten), 10));
    }
}
