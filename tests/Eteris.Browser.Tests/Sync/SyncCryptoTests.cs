using System.Security.Cryptography;
using System.Text;
using Eteris.Sync.Crypto;
using Eteris.Sync.Format;
using Eteris.Sync.Model;

namespace Eteris.Browser.Tests.Sync;

public class SyncCryptoTests
{
    [Fact]
    public void RecoveryCode_RoundTrips_AndHas13GroupsPlusCheck()
    {
        for (var i = 0; i < 50; i++)
        {
            var secret = RandomNumberGenerator.GetBytes(32);
            var code = RecoveryCode.Encode(secret);
            Assert.Matches("^([0-9A-HJKMNP-TV-Z]{4}-){13}[0-9A-HJKMNP-TV-Z]{4}$", code);
            Assert.Equal(secret, RecoveryCode.Decode(code));
        }
    }

    [Fact]
    public void RecoveryCode_IsReadWithTolerance()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var code = RecoveryCode.Encode(secret);
        // Minúsculas, espaços em vez de hífenes, e as letras que se confundem com algarismos.
        var typed = code.ToLowerInvariant().Replace('-', ' ').Replace('0', 'o').Replace('1', 'l');
        Assert.Equal(secret, RecoveryCode.Decode("  " + typed + "\n"));
    }

    [Fact]
    public void RecoveryCode_RejectsEveryCharacterTypo()
    {
        var code = RecoveryCode.Encode(RandomNumberGenerator.GetBytes(32));
        var plain = RecoveryCode.Normalize(code);
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var accepted = 0;
        for (var i = 0; i < plain.Length; i++)
        {
            var wrong = alphabet[(alphabet.IndexOf(plain[i]) + 7) % 32];
            var typo = plain[..i] + wrong + plain[(i + 1)..];
            if (RecoveryCode.TryDecode(typo, out _)) accepted++;
        }
        Assert.Equal(0, accepted);
        Assert.False(RecoveryCode.TryDecode(plain[..^4], out _));
        var e = Assert.Throws<FormatException>(() => RecoveryCode.Decode(plain[..8] + "U" + plain[9..]));
        Assert.Contains("grupo 3", e.Message);
    }

    [Fact]
    public void KeyRing_OpensOnlyWithTheRightCode_AndDetectsATamperedEnvelope()
    {
        using var secret = AccountSecret.Create();
        var ring = KeyRing.Create(secret, 1000);
        var envelope = KeyEnvelope.Parse(ring.Envelope.ToBytes());
        using var again = AccountSecret.FromCode(secret.ToCode());
        Assert.Equal(ring.OpaqueId("bookmarks", "https://a.pt/"), KeyRing.Open(again, envelope).OpaqueId("bookmarks", "https://a.pt/"));

        using var other = AccountSecret.Create();
        Assert.Throws<WrongCodeException>(() => KeyRing.Open(other, envelope));
        // Mesmo forçando a cadeia, a chave embrulhada não se desembrulha com a KEK de outro código.
        Assert.Throws<WrongCodeException>(() => KeyRing.Open(other, envelope with { ChainId = other.ChainId }));
        // Geração trocada: falha a verificação (a geração faz parte dos dados associados).
        Assert.Throws<WrongCodeException>(() => KeyRing.Open(secret, envelope with { Generation = 2 }));
        Assert.Throws<UnsupportedFormatException>(() => KeyRing.Open(secret, envelope with { Format = 9 }));
    }

    [Fact]
    public void OpaqueIds_AreStablePerAccount_AndUnlinkableAcrossAccounts()
    {
        using var a = AccountSecret.Create();
        using var b = AccountSecret.Create();
        var ringA = KeyRing.Create(a, 0);
        var ringB = KeyRing.Create(b, 0);
        var id = ringA.OpaqueId("bookmarks", "https://github.com/");
        Assert.Equal(id, ringA.OpaqueId("bookmarks", "https://github.com/"));
        Assert.NotEqual(id, ringA.OpaqueId("settings", "https://github.com/"));
        Assert.NotEqual(id, ringB.OpaqueId("bookmarks", "https://github.com/"));
        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.Matches("^[0-9a-f]{32}$", a.ChainId);
    }

    [Fact]
    public void SealedFile_PadsTo1KiB_HidesContent_AndBindsTheContext()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var json = Encoding.UTF8.GetBytes("""{"title":"Banco do Brasil — extrato","url":"https://bb.com.br/"}""");
        var file = SealedFile.Seal(key, SealedFile.Batch, "cadeia", 1, "d/x/1", json);
        var header = SealedFile.ReadHeader(file);
        Assert.Equal(0, (file.Length - header.Length - 12 - 16) % SealedFile.PadBlock);
        Assert.DoesNotContain("Banco", Encoding.Latin1.GetString(file));
        Assert.Equal(json, SealedFile.Open(key, SealedFile.Batch, "cadeia", "d/x/1", file));

        // Mudar o ficheiro de lugar (outro aparelho, outro número, outra cadeia) ou de tipo falha.
        Assert.ThrowsAny<CryptographicException>(() => SealedFile.Open(key, SealedFile.Batch, "cadeia", "d/x/2", file));
        Assert.ThrowsAny<CryptographicException>(() => SealedFile.Open(key, SealedFile.Batch, "outra", "d/x/1", file));
        Assert.Throws<FormatException>(() => SealedFile.Open(key, SealedFile.Snapshot, "cadeia", "d/x/1", file));
        // Cada byte (cabeçalho incluído) está protegido.
        for (var i = 0; i < file.Length; i += 97)
        {
            var copy = file.ToArray();
            copy[i] ^= 0x20;
            Assert.ThrowsAny<Exception>(() => SealedFile.Open(key, SealedFile.Batch, "cadeia", "d/x/1", copy));
        }
    }

    [Fact]
    public void SealedFile_SameSizeForSimilarContent()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var a = SealedFile.Seal(key, SealedFile.Batch, "c", 1, "x", Encoding.UTF8.GetBytes("""{"t":"um favorito"}"""));
        var b = SealedFile.Seal(key, SealedFile.Batch, "c", 1, "x", Encoding.UTF8.GetBytes("""{"t":"um favorito com um título bem maior"}"""));
        Assert.Equal(a.Length, b.Length);
    }

    [Fact]
    public void SealedFile_FromANewerEteris_IsReportedNotMisread()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var future = SealedFile.Seal(key, SealedFile.Batch, "c", 1, "x", "{}"u8, version: SealedFile.FormatVersion + 1);
        var e = Assert.Throws<UnsupportedFormatException>(() => SealedFile.Open(key, SealedFile.Batch, "c", "x", future));
        Assert.Contains("atualize o Éteris", e.Message);
        Assert.Throws<FormatException>(() => SealedFile.Open(key, SealedFile.Batch, "c", "x", Encoding.UTF8.GetBytes("<html>não é nosso</html>....................................")));
    }

    [Fact]
    public void Hlc_OrdersByWallThenCounterThenNode_AndSurvivesText()
    {
        var a = new Hlc(1000, 0, "a");
        Assert.True(a < new Hlc(1000, 1, "a"));
        Assert.True(new Hlc(1000, 1, "a") < new Hlc(1000, 1, "b"));
        Assert.True(new Hlc(999, 9, "z") < a);
        Assert.Equal(new Hlc(1730000000000, 12, "ab-cd"), Hlc.Parse(new Hlc(1730000000000, 12, "ab-cd").ToString()));
    }

    [Fact]
    public void HybridClock_StaysAfterWhatItSaw_EvenWithAClockBehind()
    {
        long now = 5_000;
        var clock = new HybridClock("lento", () => now);
        var remote = new Hlc(9_000_000, 3, "rapido"); // o outro aparelho está muito adiantado
        clock.Observe(remote);
        var mine = clock.Now();
        Assert.True(mine > remote);
        Assert.True(clock.Now() > mine);
        now = 10_000_000; // o relógio físico ultrapassa: volta a mandar
        Assert.Equal(10_000_000, clock.Now().Wall);
    }
}
