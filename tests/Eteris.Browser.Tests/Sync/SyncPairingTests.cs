using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Eteris.Sync.Crypto;
using Eteris.Sync.Format;
using Eteris.Sync.Merge;
using Eteris.Sync.Model;
using Eteris.Sync.Pairing;
using Eteris.Sync.Transport;

namespace Eteris.Browser.Tests.Sync;

public class SyncPairingTests
{
    private static readonly Func<TimeSpan, CancellationToken, Task> NoWait = async (_, _) => await Task.Delay(1);
    private long _now = 1_762_560_000_000;

    private long Now() => _now;

    [Fact]
    public async Task QrPairing_HandsTheSecretOver_AndBothScreensShowTheSameCode()
    {
        var cloud = new MemoryTransport();
        using var secret = AccountSecret.Create();
        using var host = await PairingHost.StartAsync(cloud, Now, NoWait);
        using var guest = await PairingGuest.JoinAsync(cloud, host.QrText, "Telemóvel", Now, NoWait);
        var hostCode = host.WaitForGuestAsync();
        var guestCode = await guest.WaitForCodeAsync();
        Assert.Equal(await hostCode, guestCode);
        Assert.Matches("^\\d{3} \\d{3}$", guestCode);
        Assert.Equal("Telemóvel", host.GuestName);

        await host.ApproveAsync(secret, "PC da sala");
        guest.ConfirmCode();
        using var received = await guest.WaitForSecretAsync();
        Assert.Equal(secret.ChainId, received.ChainId);
        Assert.Equal("PC da sala", guest.HostName);
        await host.CleanUpAsync();
        Assert.Empty(cloud.Files);
    }

    [Fact]
    public async Task ShortCode_WorksWithoutACamera()
    {
        var cloud = new MemoryTransport();
        using var secret = AccountSecret.Create();
        using var host = await PairingHost.StartAsync(cloud, Now, NoWait);
        Assert.Matches("^([0-9A-Z]{4}-){3}[0-9A-Z]{4}$", host.ShortCode);
        using var guest = await PairingGuest.JoinAsync(cloud, host.ShortCode.ToLowerInvariant(), "Portátil", Now, NoWait);
        var hostCode = host.WaitForGuestAsync();
        Assert.Equal(await guest.WaitForCodeAsync(), await hostCode);
        await host.ApproveAsync(secret, "PC");
        guest.ConfirmCode();
        using var received = await guest.WaitForSecretAsync();
        Assert.Equal(secret.ToCode(), received.ToCode());
    }

    [Fact]
    public async Task ShortCode_TransportPosingAsTheHost_GetsNothingUntilTheGuestConfirmsTheCode()
    {
        // Código curto (PC↔PC): a chave do anfitrião vem do transporte. Quem o controla põe a SUA chave no passo 1 e
        // faz de anfitrião só para o convidado, escrevendo logo o passo 5 com a SUA conta.
        var cloud = new MemoryTransport();
        var attackerView = new MemoryTransport();
        using var realHost = await PairingHost.StartAsync(cloud, Now, NoWait);
        using var attackerSecret = AccountSecret.Create();
        using var fakeHost = await PairingHost.StartAsync(attackerView, realHost.Session, Now, NoWait, default);
        using var guest = await PairingGuest.JoinAsync(attackerView, realHost.ShortCode, "Portátil", Now, NoWait);
        var fakeCode = fakeHost.WaitForGuestAsync();
        var guestCode = await guest.WaitForCodeAsync();
        Assert.Equal(await fakeCode, guestCode);
        await fakeHost.ApproveAsync(attackerSecret, "PC do atacante");

        // O passo 5 do atacante já lá está, mas o convidado não o abre sem o utilizador confirmar NESTE ecrã que o
        // código é igual ao do anfitrião verdadeiro (que mostra outro, ou nenhum). O utilizador não confirma.
        await Assert.ThrowsAsync<InvalidOperationException>(() => guest.WaitForSecretAsync());
        Assert.Null(guest.HostName);
        // E ConfirmCode antes de haver código não serve de atalho.
        using var early = await PairingGuest.JoinAsync(attackerView, realHost.ShortCode, "x", Now, NoWait);
        Assert.Throws<InvalidOperationException>(early.ConfirmCode);
    }

    [Fact]
    public async Task GuestName_NeverTravelsInClear()
    {
        var cloud = new MemoryTransport();
        using var secret = AccountSecret.Create();
        using var host = await PairingHost.StartAsync(cloud, Now, NoWait);
        using var guest = await PairingGuest.JoinAsync(cloud, host.ShortCode, "Portátil da Inês", Now, NoWait);
        var hostCode = host.WaitForGuestAsync();
        await guest.WaitForCodeAsync();
        await hostCode;
        await host.ApproveAsync(secret, "PC da sala");
        // Nada foi limpo (fecho brusco do anfitrião): mesmo assim nenhum ficheiro tem os nomes.
        foreach (var (_, bytes) in cloud.Files)
        {
            var text = Encoding.Latin1.GetString(bytes) + Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain("Inês", text);
            Assert.DoesNotContain("In\u00EAs", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PC da sala", text);
        }
        Assert.Equal("Portátil da Inês", host.GuestName);
    }

    // Dois aparelhos ligados respondem ao mesmo pedido (os dois o viram antes de algum escrever o passo 1): o convidado
    // compromete-se com a chave de um; o outro desiste calado, sem a recusa que fazia falhar o emparelhamento certo.
    [Fact]
    public async Task TwoHostsAnswerTheSameRequest_TheOneNotChosenStepsAsideQuietly()
    {
        var cloud = new MemoryTransport();
        using var secret = AccountSecret.Create();
        var session = await PairingRequests.OpenAsync(cloud, Now);
        using var first = await PairingHost.AnswerAsync(cloud, session, Now, NoWait);
        using var guest = await PairingRequests.WaitForHostAsync(cloud, session, "Novo", Now, NoWait, TimeSpan.Zero);
        using var second = await PairingHost.AnswerAsync(cloud, session, Now, NoWait); // chega depois e reescreve o passo 1
        var secondCode = second.WaitForGuestAsync();
        var firstCode = first.WaitForGuestAsync();
        await Assert.ThrowsAsync<PairingAnsweredElsewhereException>(() => secondCode);
        var guestCode = await guest.WaitForCodeAsync();
        Assert.Equal(guestCode, await firstCode);
        Assert.Null(await cloud.ReadAsync(SyncPaths.Pairing(session) + "x-host"));
        guest.ConfirmCode();
        await first.ApproveAsync(secret, "Casa");
        using var received = await guest.WaitForSecretAsync();
        Assert.Equal(secret.ChainId, received.ChainId);
    }

    [Fact]
    public void PublicKeys_OnlyP256()
    {
        using var p256 = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using (PairingProtocol.ImportPublic(p256.PublicKey.ExportSubjectPublicKeyInfo())) { }
        using var brainpool = ECDiffieHellman.Create(ECCurve.NamedCurves.brainpoolP256r1);
        Assert.Equal(256, brainpool.KeySize);
        Assert.Throws<PairingException>(() => PairingProtocol.ImportPublic(brainpool.PublicKey.ExportSubjectPublicKeyInfo()));
        using var p384 = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<PairingException>(() => PairingProtocol.ImportPublic(p384.PublicKey.ExportSubjectPublicKeyInfo()));
        Assert.Throws<PairingException>(() => PairingProtocol.ImportPublic([1, 2, 3]));
    }

    [Fact]
    public async Task NothingOnTheQrOrTheTransport_RevealsTheSecret()
    {
        var cloud = new MemoryTransport();
        using var secret = AccountSecret.Create();
        using var host = await PairingHost.StartAsync(cloud, Now, NoWait);
        var qr = host.QrText;
        using var guest = await PairingGuest.JoinAsync(cloud, qr, "Telemóvel", Now, NoWait);
        var hostCode = host.WaitForGuestAsync();
        await guest.WaitForCodeAsync();
        await hostCode;
        await host.ApproveAsync(secret, "PC");

        var code = secret.ToCode().Replace("-", "");
        var raw = secret.ToBytes();
        var b64 = Kdf.Base64Url(raw);
        Assert.DoesNotContain(code, qr);
        Assert.DoesNotContain(b64, qr);
        foreach (var (_, bytes) in cloud.Files)
        {
            var text = Encoding.Latin1.GetString(bytes);
            Assert.DoesNotContain(b64, text);
            Assert.DoesNotContain(code, text);
            Assert.False(bytes.AsSpan().IndexOf(raw) >= 0, "segredo em claro no transporte");
        }

        // Quem fotografou o QR e lê o transporte (mas não tem a chave privada de nenhum dos dois) não decifra o passo 5,
        // nem juntando-se agora: a sessão já tem convidado e o código não lhe serve.
        using var spy = await PairingGuest.JoinAsync(cloud, qr, "Espião", Now, NoWait);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await spy.WaitForCodeAsync();
            spy.ConfirmCode();
            using var stolen = await spy.WaitForSecretAsync();
        });
    }

    [Fact]
    public async Task ManInTheMiddle_GetsADifferentCode_AndTheUserRefuses()
    {
        // O atacante controla o transporte: entrega ao anfitrião a sua própria chave em vez da do convidado.
        var cloud = new MemoryTransport();
        var attackerView = new MemoryTransport();
        using var host = await PairingHost.StartAsync(cloud, Now, NoWait);
        foreach (var (path, bytes) in cloud.Files) await attackerView.WriteAsync(path, bytes);
        using var realGuest = await PairingGuest.JoinAsync(attackerView, host.QrText, "Telemóvel", Now, NoWait);
        using var attacker = await PairingGuest.JoinAsync(cloud, host.QrText, "Telemóvel", Now, NoWait); // finge ser o telemóvel
        var hostCode = host.WaitForGuestAsync();
        var attackerCode = await attacker.WaitForCodeAsync();
        var hostShows = await hostCode;
        Assert.Equal(attackerCode, hostShows);
        // O telemóvel verdadeiro fala com o atacante (que reencaminha o nonce do anfitrião): o seu ecrã mostra outro código.
        await attackerView.WriteAsync(SyncPaths.Pairing(host.Session) + "3-host", cloud.Files[SyncPaths.Pairing(host.Session) + "3-host"]);
        var realShows = await realGuest.WaitForCodeAsync();
        Assert.NotEqual(hostShows, realShows);

        // O utilizador vê códigos diferentes e recusa: nada é entregue, e o convidado fica a saber.
        await host.RefuseAsync();
        attacker.ConfirmCode(); // o atacante confirma o que quiser: não lhe serve de nada
        await Assert.ThrowsAsync<PairingException>(() => attacker.WaitForSecretAsync());
    }

    [Fact]
    public async Task QrWithASwappedKey_IsRefused()
    {
        var cloud = new MemoryTransport();
        using var host = await PairingHost.StartAsync(cloud, Now, NoWait);
        using var other = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var forged = PairingProtocol.QrText(host.Session, other.PublicKey.ExportSubjectPublicKeyInfo());
        var e = await Assert.ThrowsAsync<PairingException>(() => PairingGuest.JoinAsync(cloud, forged, "x", Now, NoWait));
        Assert.Contains("não confere com o QR", e.Message);
    }

    [Fact]
    public async Task TamperedSecretMessage_IsRefused()
    {
        var cloud = new MemoryTransport();
        using var secret = AccountSecret.Create();
        using var host = await PairingHost.StartAsync(cloud, Now, NoWait);
        using var guest = await PairingGuest.JoinAsync(cloud, host.QrText, "x", Now, NoWait);
        var hostCode = host.WaitForGuestAsync();
        await guest.WaitForCodeAsync();
        await hostCode;
        await host.ApproveAsync(secret, "PC");
        var path = SyncPaths.Pairing(host.Session) + "5-host";
        var bytes = cloud.Files[path].ToArray();
        bytes[^3] ^= 1;
        cloud.Replace(path, bytes);
        guest.ConfirmCode();
        await Assert.ThrowsAsync<PairingException>(() => guest.WaitForSecretAsync());
    }

    [Fact]
    public async Task Pairing_ExpiresAfterTenMinutes()
    {
        var cloud = new MemoryTransport();
        using var host = await PairingHost.StartAsync(cloud, Now, NoWait);
        _now += (long)TimeSpan.FromMinutes(11).TotalMilliseconds;
        await Assert.ThrowsAsync<PairingException>(() => PairingGuest.JoinAsync(cloud, host.QrText, "x", Now, NoWait));
        await Assert.ThrowsAsync<PairingException>(() => host.WaitForGuestAsync());
    }

    [Fact]
    public void Invitation_RejectsGarbage()
    {
        Assert.Throws<FormatException>(() => PairingProtocol.ParseInvitation("eteris-par:9:AAAA:BBBB"));
        Assert.Throws<FormatException>(() => PairingProtocol.ParseInvitation("https://exemplo.pt/"));
        Assert.Throws<FormatException>(() => PairingProtocol.ParseInvitation("ABCD-EFGH"));
    }

    // ---- notas: fusão a três -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("a\nb\nc", "a\nB\nc", "a\nb\nc", "a\nB\nc")]                  // só um lado mudou
    [InlineData("a\nb\nc", "x\na\nb\nc", "a\nb\nc\ny", "x\na\nb\nc\ny")]      // acrescentos nas pontas
    [InlineData("a\nb\nc", "a\nc", "a\nb\nc\nd", "a\nc\nd")]                  // apagar de um lado, acrescentar do outro
    [InlineData("a\nb\nc", "a\nZ\nc", "a\nZ\nc", "a\nZ\nc")]                  // a mesma mudança dos dois lados
    [InlineData("", "nova", "", "nova")]
    public void NotesMerge_CleanCases(string baseText, string first, string second, string expected)
    {
        var result = NotesMerge.Merge(baseText, first, second);
        Assert.Equal(expected, result.Text);
        Assert.Equal(0, result.Conflicts);
        // Simetria: trocar os lados dá o mesmo texto quando não há conflito.
        Assert.Equal(expected, NotesMerge.Merge(baseText, second, first).Text);
    }

    [Fact]
    public void NotesMerge_Conflict_KeepsBothSidesInTheGivenOrder()
    {
        var result = NotesMerge.Merge("x\nmeio\ny", "x\nmeio A\ny", "x\nmeio B\ny", "Casa", "Trabalho");
        Assert.Equal(1, result.Conflicts);
        var lines = result.Text.Split('\n');
        Assert.Equal("x", lines[0]);
        Assert.Equal("y", lines[^1]);
        Assert.True(Array.IndexOf(lines, "meio A") < Array.IndexOf(lines, "meio B"));
        Assert.Contains("——— Casa ———", lines);
        Assert.Contains("——— Trabalho ———", lines);
        Assert.DoesNotContain("meio", lines);
    }

    [Fact]
    public void NotesMerge_HugeTexts_DoNotHang()
    {
        var baseText = string.Join('\n', Enumerable.Range(0, 5000).Select(i => "linha " + i));
        var a = baseText.Replace("linha 10\n", "linha dez\n");
        var b = string.Join('\n', Enumerable.Range(0, 5000).Select(i => i % 2 == 0 ? "par " + i : "linha " + i));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = NotesMerge.Merge(baseText, a, b);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Contains("par 4998", result.Text);
    }

    // ---- perfil exportado ----------------------------------------------------------------------------------------------

    [Fact]
    public void ProfileArchive_RoundTrips_WithTheCodeOnly()
    {
        using var secret = AccountSecret.Create();
        var records = new[]
        {
            ProfileArchive.Record(Collections.Bookmarks, "https://x.pt/", new JsonObject { ["title"] = "Título secreto" }, 1000),
            ProfileArchive.Record(Collections.Notes, Collections.NotesKey, new JsonObject { ["text"] = "notas privadas" }, 1000),
        };
        var file = ProfileArchive.Export(records, secret, "PC da sala", 1000);
        var text = Encoding.Latin1.GetString(file);
        Assert.DoesNotContain("secreto", text);
        Assert.DoesNotContain("privadas", text);
        Assert.DoesNotContain("PC da sala", text);

        var back = ProfileArchive.Import(file, secret.ToCode().ToLowerInvariant());
        Assert.Equal("PC da sala", back.DeviceName);
        Assert.Equal("Título secreto", back.Records[0].Text("title"));

        using var other = AccountSecret.Create();
        Assert.Throws<WrongCodeException>(() => ProfileArchive.Import(file, other.ToCode()));
        var bad = file.ToArray();
        bad[^30] ^= 4;
        Assert.Throws<WrongCodeException>(() => ProfileArchive.Import(bad, secret.ToCode()));
        Assert.Throws<FormatException>(() => ProfileArchive.Import(file, "ABCD"));
        Assert.Throws<FormatException>(() => ProfileArchive.Import(Encoding.UTF8.GetBytes("<!DOCTYPE NETSCAPE-Bookmark-file-1> ............................"), secret.ToCode()));
    }

    [Fact]
    public void ProfileArchive_TwoExportsOfTheSameProfile_ShareNoKey()
    {
        using var secret = AccountSecret.Create();
        var records = new[] { ProfileArchive.Record(Collections.Bookmarks, "https://x.pt/", new JsonObject { ["title"] = "x" }, 1) };
        var one = ProfileArchive.Export(records, secret, "PC", 1);
        var two = ProfileArchive.Export(records, secret, "PC", 1);
        Assert.NotEqual(SealedFile.ReadHeader(one).Public, SealedFile.ReadHeader(two).Public);
        Assert.NotEqual(one[^64..], two[^64..]);
    }
}
