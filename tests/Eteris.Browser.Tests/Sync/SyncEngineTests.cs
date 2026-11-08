using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Eteris.Sync.Crypto;
using Eteris.Sync.Engine;
using Eteris.Sync.Format;
using Eteris.Sync.Model;
using Eteris.Sync.Transport;

namespace Eteris.Browser.Tests.Sync;

// Aparelhos simulados com relógios próprios (que podem estar errados) num transporte partilhado.
internal sealed class SyncLab : IDisposable
{
    public long Now = 1_762_560_000_000; // 08/11/2025, um instante fixo
    public readonly MemoryTransport Cloud = new();
    public readonly AccountSecret Secret = AccountSecret.Create();
    private readonly Dictionary<string, long> _skew = new();

    public Func<long> ClockOf(string name) => () => Now + _skew.GetValueOrDefault(name);

    public void Skew(string name, TimeSpan offset) => _skew[name] = (long)offset.TotalMilliseconds;

    public void Advance(TimeSpan time) => Now += (long)time.TotalMilliseconds;

    public async Task<SyncEngine[]> DevicesAsync(params string[] names)
    {
        var list = new List<SyncEngine>
        {
            await SyncEngine.CreateAsync(Cloud, Secret, DeviceIdentity.Create(names[0]), ClockOf(names[0])),
        };
        foreach (var name in names.Skip(1))
            list.Add(await SyncEngine.JoinAsync(Cloud, AccountSecret.FromCode(Secret.ToCode()), DeviceIdentity.Create(name), ClockOf(name)));
        return [.. list];
    }

    // Rondas até ninguém ter nada para enviar (como o temporizador da sincronização faria).
    public static async Task SettleAsync(params SyncEngine[] devices)
    {
        for (var round = 0; round < 6; round++)
        {
            foreach (var device in devices) await device.SyncAsync();
            if (devices.All(d => d.State.Outbox.Count == 0 && d.State.Pending is null)) break;
        }
        foreach (var device in devices) await device.PullAsync();
    }

    public void Dispose() => Secret.Dispose();
}

public class SyncEngineTests
{
    private static JsonObject Bookmark(string title) => new() { ["title"] = title };

    [Fact]
    public async Task ThreeDevices_ConcurrentEdits_Converge()
    {
        using var lab = new SyncLab();
        var (casa, trabalho, portatil) = await Three(lab);
        casa.Put(Collections.Bookmarks, "https://pt.wikipedia.org/", Bookmark("Wikipédia"));
        casa.Put(Collections.Bookmarks, "https://github.com/", Bookmark("GitHub"));
        casa.Put(Collections.Bookmarks, "https://g1.globo.com/", Bookmark("g1"));
        casa.Put(Collections.Settings, "theme", new JsonObject { ["value"] = "dark" });
        casa.Put(Collections.Shield, "off|exemplo.pt|", new JsonObject());
        await SyncLab.SettleAsync(casa, trabalho, portatil);
        Assert.Equal(3, portatil.Records(Collections.Bookmarks).Count);

        // Sem rede entre eles: três aparelhos mexem no mesmo e em coisas diferentes.
        casa.Put(Collections.Bookmarks, "https://github.com/", Bookmark("GitHub (casa)"));
        lab.Advance(TimeSpan.FromSeconds(1));
        trabalho.Delete(Collections.Bookmarks, "https://g1.globo.com/");
        trabalho.Put(Collections.Bookmarks, "https://github.com/", Bookmark("GitHub (trabalho, mais recente)"));
        lab.Advance(TimeSpan.FromSeconds(1));
        portatil.Put(Collections.Settings, "theme", new JsonObject { ["value"] = "light" });
        portatil.Delete(Collections.Shield, "off|exemplo.pt|");
        portatil.Put(Collections.Spaces, "trabalho", new JsonObject { ["name"] = "Trabalho", ["color"] = 3 });

        await SyncLab.SettleAsync(portatil, casa, trabalho);

        Assert.Equal(casa.Fingerprint(), trabalho.Fingerprint());
        Assert.Equal(casa.Fingerprint(), portatil.Fingerprint());
        Assert.Equal("GitHub (trabalho, mais recente)", casa.Get(Collections.Bookmarks, "https://github.com/")!.Text("title"));
        Assert.True(casa.Get(Collections.Bookmarks, "https://g1.globo.com/")!.Deleted);
        Assert.Equal("light", trabalho.Get(Collections.Settings, "theme")!.Text("value"));
        Assert.True(trabalho.Get(Collections.Shield, "off|exemplo.pt|")!.Deleted);
        Assert.Equal("Trabalho", casa.Get(Collections.Spaces, "trabalho")!.Text("name"));
    }

    [Fact]
    public async Task OrderOfArrival_DoesNotMatter()
    {
        // Os mesmos lotes lidos por ordens diferentes dão o mesmo estado.
        using var lab = new SyncLab();
        var (a, b, c) = await Three(lab);
        for (var i = 0; i < 20; i++)
        {
            var who = (i % 3) switch { 0 => a, 1 => b, _ => c };
            who.Put(Collections.Bookmarks, $"https://site{i % 7}.pt/", Bookmark($"v{i}"));
            if (i % 5 == 4) who.Delete(Collections.Bookmarks, $"https://site{(i + 2) % 7}.pt/");
            lab.Advance(TimeSpan.FromMilliseconds(i % 2)); // empates de relógio de propósito
            await who.PushAsync();
        }
        await c.PullAsync();
        await b.PullAsync();
        await a.PullAsync();
        Assert.Equal(a.Fingerprint(), b.Fingerprint());
        Assert.Equal(a.Fingerprint(), c.Fingerprint());
    }

    [Fact]
    public async Task ClockBehind_CannotLoseAnEditMadeAfterSeeingTheOther()
    {
        using var lab = new SyncLab();
        lab.Skew("atrasado", TimeSpan.FromHours(-5));
        var devices = await lab.DevicesAsync("certo", "atrasado");
        devices[0].Put(Collections.Settings, "engine", new JsonObject { ["value"] = "brave" });
        await SyncLab.SettleAsync(devices);
        // O atrasado muda DEPOIS de ver a alteração: tem de ganhar, apesar do relógio 5 horas atrás.
        devices[1].Put(Collections.Settings, "engine", new JsonObject { ["value"] = "startpage" });
        await SyncLab.SettleAsync(devices);
        Assert.Equal("startpage", devices[0].Get(Collections.Settings, "engine")!.Text("value"));
    }

    [Fact]
    public async Task Cloud_SeesNoPlaintext_InAnyByte()
    {
        using var lab = new SyncLab();
        var (a, b, c) = await Three(lab);
        a.Put(Collections.Bookmarks, "https://meubanco.example/extrato", Bookmark("Extrato do Banco Segredo"));
        b.Put(Collections.Settings, "engine", new JsonObject { ["value"] = "startpage" });
        c.SetNotes("Lista de compras: abacaxi\nSenha do Wi-Fi da avó: pinguim42");
        c.PublishTabs(new JsonObject { ["tabs"] = new JsonArray(new JsonObject { ["url"] = "https://consulta-medica.example/" }) });
        c.Put(Collections.Spaces, "esp", new JsonObject { ["name"] = "Mudança de emprego" });
        await SyncLab.SettleAsync(a, b, c);
        await a.CompactAsync();
        await SyncLab.SettleAsync(a, b, c);

        string[] secrets = ["meubanco", "Extrato", "Segredo", "startpage", "abacaxi", "pinguim42", "consulta-medica", "Mudança",
            "https://", "title", "bookmarks", "notes", lab.Secret.ToCode().Replace("-", ""), a.Device.Name, b.Device.Name];
        var everything = string.Join("\n", lab.Cloud.Files.Select(f => f.Key + "\n" + Encoding.Latin1.GetString(f.Value)
                                                                         + "\n" + Encoding.UTF8.GetString(f.Value)));
        foreach (var secret in secrets) Assert.DoesNotContain(secret, everything, StringComparison.OrdinalIgnoreCase);
        // E também em UTF-16 (por precaução): nenhum texto do utilizador escrito como string .NET.
        var utf16 = string.Join("\n", lab.Cloud.Files.Values.Select(Encoding.Unicode.GetString));
        foreach (var secret in secrets.Take(8)) Assert.DoesNotContain(secret, utf16, StringComparison.OrdinalIgnoreCase);
        Assert.All(lab.Cloud.Files.Keys, name => Assert.Matches("^eteris/[0-9a-f]{32}/(keys|d/[0-9a-f]{32}/\\d{12}|s/[0-9a-f]{32}-\\d{12}|a/[0-9a-f]{32})$", name));
    }

    [Fact]
    public async Task TamperedBatch_IsRefusedAndRetried_NeverMarkedRead()
    {
        using var lab = new SyncLab();
        var devices = await lab.DevicesAsync("A", "B");
        devices[0].Put(Collections.Bookmarks, "https://a.pt/", Bookmark("a"));
        await devices[0].PushAsync();
        devices[0].Put(Collections.Bookmarks, "https://b.pt/", Bookmark("b"));
        await devices[0].PushAsync();
        var first = SyncPaths.Batch(devices[0].ChainId, devices[0].Device.Id, 1);
        var original = lab.Cloud.Files[first];
        var bad = original.ToArray();
        bad[^20] ^= 0x01;
        lab.Cloud.Replace(first, bad);

        var result = await devices[1].PullAsync();
        Assert.Contains(devices[0].Device.Id, result.Pending.Keys);
        Assert.Empty(devices[1].Records(Collections.Bookmarks)); // o lote 2 espera pelo 1
        Assert.Equal(0, devices[1].State.Read.GetValueOrDefault(devices[0].Device.Id));

        // Um lote de outro aparelho posto no lugar deste (troca de ficheiros) também é recusado.
        lab.Cloud.Replace(first, lab.Cloud.Files[SyncPaths.Batch(devices[0].ChainId, devices[0].Device.Id, 2)]);
        Assert.NotEmpty((await devices[1].PullAsync()).Pending);

        lab.Cloud.Replace(first, original); // a nuvem acabou de trazer o ficheiro certo
        result = await devices[1].PullAsync();
        Assert.Empty(result.Pending);
        Assert.Equal(2, devices[1].Records(Collections.Bookmarks).Count);
    }

    [Fact]
    public async Task OutOfOrderArrival_WaitsForTheMissingBatch()
    {
        using var lab = new SyncLab();
        var devices = await lab.DevicesAsync("A", "B");
        devices[0].Put(Collections.Bookmarks, "https://1.pt/", Bookmark("1"));
        await devices[0].PushAsync();
        devices[0].Put(Collections.Bookmarks, "https://2.pt/", Bookmark("2"));
        await devices[0].PushAsync();
        var path = SyncPaths.Batch(devices[0].ChainId, devices[0].Device.Id, 1);
        var held = lab.Cloud.Files[path];
        await lab.Cloud.DeleteAsync(path); // o OneDrive trouxe o 2 antes do 1
        var result = await devices[1].PullAsync();
        Assert.Contains("à espera do lote 1", result.Pending.Values.Single());
        await lab.Cloud.WriteAsync(path, held);
        await devices[1].PullAsync();
        Assert.Equal(2, devices[1].Records(Collections.Bookmarks).Count);
    }

    [Fact]
    public async Task FailedUpload_IsResentIdentically_WithTheSameNumber()
    {
        using var lab = new SyncLab();
        var flaky = new FlakyTransport(lab.Cloud);
        var a = await SyncEngine.CreateAsync(flaky, lab.Secret, DeviceIdentity.Create("A"), lab.ClockOf("A"));
        a.Put(Collections.Bookmarks, "https://x.pt/", Bookmark("x"));
        flaky.FailWrites = 1;
        await Assert.ThrowsAsync<IOException>(() => a.PushAsync());
        a.Put(Collections.Bookmarks, "https://y.pt/", Bookmark("y")); // entretanto, mais uma alteração
        Assert.Equal(2, await a.PushAsync());
        var b = await SyncEngine.JoinAsync(lab.Cloud, lab.Secret, DeviceIdentity.Create("B"), lab.ClockOf("B"));
        await b.PullAsync();
        Assert.Equal(2, b.Records(Collections.Bookmarks).Count);
        Assert.Equal(2, lab.Cloud.Files.Keys.Count(k => k.Contains("/d/")));
    }

    [Fact]
    public async Task Tombstones_KeepDeletesDeleted_UntilTheirDeadline()
    {
        using var lab = new SyncLab();
        var devices = await lab.DevicesAsync("A", "B");
        var (a, b) = (devices[0], devices[1]);
        a.Put(Collections.Bookmarks, "https://velho.pt/", Bookmark("velho"));
        await SyncLab.SettleAsync(a, b);
        b.Delete(Collections.Bookmarks, "https://velho.pt/");
        await SyncLab.SettleAsync(a, b);
        Assert.Empty(a.Records(Collections.Bookmarks));
        Assert.True(a.Get(Collections.Bookmarks, "https://velho.pt/")!.Deleted); // a lápide fica

        lab.Advance(TimeSpan.FromDays(91));
        await SyncLab.SettleAsync(a, b);
        await a.CompactAsync();
        Assert.Null(a.Get(Collections.Bookmarks, "https://velho.pt/")); // passado o prazo, a lápide sai
    }

    [Fact]
    public async Task DeviceAbsentLongerThanTheDeadline_DoesNotResurrectDeletes()
    {
        using var lab = new SyncLab();
        var (a, b, c) = await Three(lab);
        a.Put(Collections.Bookmarks, "https://apagado.pt/", Bookmark("vai sair"));
        a.Put(Collections.Bookmarks, "https://fica.pt/", Bookmark("fica"));
        await SyncLab.SettleAsync(a, b, c);

        // C fica fora 100 dias. Entretanto A apaga, todos sincronizam, e a lápide expira e sai no instantâneo.
        a.Delete(Collections.Bookmarks, "https://apagado.pt/");
        await SyncLab.SettleAsync(a, b);
        lab.Advance(TimeSpan.FromDays(100));
        await SyncLab.SettleAsync(a, b);
        await a.CompactAsync();
        await b.CompactAsync();
        Assert.Null(a.Get(Collections.Bookmarks, "https://apagado.pt/"));

        // C volta com uma alteração feita enquanto estava fora: essa fica, o apagado não volta.
        c.Put(Collections.Bookmarks, "https://novo-do-c.pt/", Bookmark("novo"));
        var result = await c.PullAsync();
        Assert.True(result.Rebased);
        await SyncLab.SettleAsync(c, a, b);
        foreach (var device in new[] { a, b, c })
        {
            Assert.Null(device.Get(Collections.Bookmarks, "https://apagado.pt/") is { Deleted: false } ? "ressuscitou" : null);
            Assert.Equal("novo", device.Get(Collections.Bookmarks, "https://novo-do-c.pt/")!.Text("title"));
            Assert.Equal("fica", device.Get(Collections.Bookmarks, "https://fica.pt/")!.Text("title"));
        }
        Assert.Equal(a.Fingerprint(), c.Fingerprint());
    }

    [Fact]
    public async Task Compaction_DeletesOnlyBatchesEveryoneHasRead_AndNewDevicesStartFromTheSnapshot()
    {
        using var lab = new SyncLab();
        var devices = await lab.DevicesAsync("A", "B");
        var (a, b) = (devices[0], devices[1]);
        await b.PullAsync(); // B já existe na conta (tem estado), mas ainda não leu nada de A
        for (var i = 0; i < 5; i++)
        {
            a.Put(Collections.Bookmarks, $"https://{i}.pt/", Bookmark($"{i}"));
            await a.PushAsync();
        }
        await a.CompactAsync();
        Assert.Equal(5, lab.Cloud.Files.Keys.Count(k => k.Contains("/d/"))); // B ainda não leu nada: tudo fica
        await b.PullAsync();
        await a.CompactAsync();
        Assert.Equal(0, lab.Cloud.Files.Keys.Count(k => k.Contains("/d/")));
        Assert.Single(lab.Cloud.Files.Keys, k => k.Contains("/s/"));

        // Um aparelho novo lê o instantâneo (os lotes já não existem) e continua daí.
        var c = await SyncEngine.JoinAsync(lab.Cloud, lab.Secret, DeviceIdentity.Create("C"), lab.ClockOf("C"));
        var result = await c.PullAsync();
        Assert.True(result.Rebased);
        Assert.Equal(5, c.Records(Collections.Bookmarks).Count);
        a.Put(Collections.Bookmarks, "https://depois.pt/", Bookmark("depois"));
        await SyncLab.SettleAsync(a, b, c);
        Assert.Equal(a.Fingerprint(), c.Fingerprint());
    }

    [Fact]
    public async Task UnknownFieldsFromANewerEteris_ArePreserved()
    {
        using var lab = new SyncLab();
        var devices = await lab.DevicesAsync("novo", "antigo");
        // O Éteris «novo» escreve um campo que o antigo não conhece, num registo com esquema 2 e um campo de topo novo.
        var record = new SyncRecord
        {
            Collection = Collections.Bookmarks,
            Id = devices[0].Get(Collections.Bookmarks, "https://x.pt/")?.Id ?? new KeyRing_Id(lab).Of(Collections.Bookmarks, "https://x.pt/"),
            Key = "https://x.pt/",
            Version = new Hlc(lab.Now, 0, devices[0].Device.Id),
            Data = new JsonObject { ["title"] = "X", ["folder"] = "Leituras/Tecnologia" },
            Schema = 2,
            Extra = new() { ["future"] = System.Text.Json.JsonDocument.Parse("""{"a":1}""").RootElement.Clone() },
        };
        devices[0].State.Records[SyncState.RecordKey(record.Collection, record.Id)] = record;
        devices[0].State.Outbox[SyncState.RecordKey(record.Collection, record.Id)] = record;
        await devices[0].PushAsync();
        await devices[1].PullAsync();

        var seen = devices[1].Get(Collections.Bookmarks, "https://x.pt/")!;
        Assert.Equal("Leituras/Tecnologia", seen.Text("folder"));
        Assert.Equal(2, seen.Schema);
        // O antigo reenvia (por exemplo num instantâneo) sem perder nada.
        await devices[1].CompactAsync();
        var fresh = await SyncEngine.JoinAsync(lab.Cloud, lab.Secret, DeviceIdentity.Create("terceiro"), lab.ClockOf("t"));
        await lab.Cloud.DeleteAsync(SyncPaths.Batch(devices[0].ChainId, devices[0].Device.Id, 1));
        await fresh.PullAsync();
        var again = fresh.Get(Collections.Bookmarks, "https://x.pt/")!;
        Assert.Equal("Leituras/Tecnologia", again.Text("folder"));
        Assert.True(again.Extra!.ContainsKey("future"));
    }

    [Fact]
    public async Task BatchWithANewerVersionByte_WaitsWithoutAskingForAnUpdate()
    {
        // A versão do cabeçalho lê-se antes de decifrar: quem controla o transporte pode mudá-la. O lote fica pendente
        // (não é lido mal nem marcado como lido), mas o aparelho NÃO diz «atualize o Éteris» por causa disso.
        using var lab = new SyncLab();
        var devices = await lab.DevicesAsync("A", "B");
        devices[0].Put(Collections.Bookmarks, "https://x.pt/", Bookmark("x"));
        await devices[0].PushAsync();
        var path = SyncPaths.Batch(devices[0].ChainId, devices[0].Device.Id, 1);
        var bytes = lab.Cloud.Files[path].ToArray();
        bytes[4] = SealedFile.FormatVersion + 1;
        lab.Cloud.Replace(path, bytes);
        var result = await devices[1].PullAsync();
        Assert.False(result.NeedsUpdate);
        Assert.DoesNotContain("atualize", result.Pending.Values.Single());
        Assert.Contains("não conhece", result.Pending.Values.Single());
        Assert.Equal(0, devices[1].State.Read.GetValueOrDefault(devices[0].Device.Id));

        // Um registo de chaves com o formato mudado à mão também não chega para pedir a atualização.
        var keysPath = SyncPaths.Keys(devices[0].ChainId);
        var forged = KeyEnvelope.Parse(lab.Cloud.Files[keysPath]) with { Format = KeyEnvelope.CurrentFormat + 1 };
        lab.Cloud.Replace(keysPath, forged.ToBytes());
        Assert.False((await devices[1].PullAsync()).NeedsUpdate);

        // Só quando o registo de chaves, autenticado pela chave da conta, anuncia um formato mais recente.
        lab.Cloud.Replace(keysPath, KeyRing.EnvelopeForFormat(lab.Secret, KeyEnvelope.CurrentFormat + 1, lab.Now).ToBytes());
        result = await devices[1].PullAsync();
        Assert.True(result.NeedsUpdate);
        Assert.Contains("atualize o Éteris", result.Pending.Values.Single());
    }

    [Fact]
    public async Task DeviceIdleLongerThanTheDeadline_KeepsWhatItSent_AndCompactionDoesNotLoseIt()
    {
        // Dois PCs. B fez um instantâneo; A envia um lote e fica 91 dias sem uso, com B desligado. Ao voltar, A recomeça
        // dos instantâneos (caso «ausente»): o que A enviou e nenhum instantâneo cobre não pode desaparecer — nem
        // daqui, nem de todo quando a compactação de A apagar o lote velho que B nunca leu.
        using var lab = new SyncLab();
        var devices = await lab.DevicesAsync("A", "B");
        var (a, b) = (devices[0], devices[1]);
        a.Put(Collections.Bookmarks, "https://antes.pt/", Bookmark("antes"));
        await SyncLab.SettleAsync(a, b);
        await b.CompactAsync();
        a.Put(Collections.Bookmarks, "https://so-do-a.pt/", Bookmark("só do A"));
        await a.SyncAsync();

        lab.Advance(TimeSpan.FromDays(91));
        var result = await a.PullAsync();
        Assert.True(result.Rebased);
        Assert.Equal("só do A", a.Get(Collections.Bookmarks, "https://so-do-a.pt/")!.Text("title"));
        await a.CompactAsync();
        Assert.Equal("só do A", a.Get(Collections.Bookmarks, "https://so-do-a.pt/")!.Text("title"));

        // Um aparelho novo (e B, quando voltar) recebe-o.
        var c = await SyncEngine.JoinAsync(lab.Cloud, lab.Secret, DeviceIdentity.Create("C"), lab.ClockOf("C"));
        await c.PullAsync();
        Assert.Equal("só do A", c.Get(Collections.Bookmarks, "https://so-do-a.pt/")!.Text("title"));
        await SyncLab.SettleAsync(b, a, c);
        Assert.Equal("só do A", b.Get(Collections.Bookmarks, "https://so-do-a.pt/")!.Text("title"));
        Assert.Equal(a.Fingerprint(), b.Fingerprint());
        Assert.Equal(a.Fingerprint(), c.Fingerprint());
    }

    [Fact]
    public async Task Rebase_KeepsALiveRecordThatANewerSnapshotSimplyNeverSaw()
    {
        // O instantâneo de A tem o favorito de C; o de B (mais recente, depois do horizonte) não o tem só porque B
        // nunca leu o lote de C — não porque foi apagado. Um aparelho novo não o pode deitar fora.
        using var lab = new SyncLab();
        var (a, b, c) = await Three(lab);
        c.Put(Collections.Bookmarks, "https://do-c.pt/", Bookmark("do C"));
        await c.PushAsync();
        await a.PullAsync();
        await a.CompactAsync();
        lab.Advance(TimeSpan.FromDays(91));
        await b.CompactAsync(); // B não leu o lote de C

        var d = await SyncEngine.JoinAsync(lab.Cloud, lab.Secret, DeviceIdentity.Create("D"), lab.ClockOf("D"));
        var result = await d.PullAsync();
        Assert.True(result.Rebased);
        Assert.Equal("do C", d.Get(Collections.Bookmarks, "https://do-c.pt/")!.Text("title"));
    }

    [Fact]
    public async Task StateRestoredFromAnOldCopy_NeverReusesBatchNumbers()
    {
        using var lab = new SyncLab();
        var flaky = new FlakyTransport(lab.Cloud);
        var a = await SyncEngine.CreateAsync(flaky, lab.Secret, DeviceIdentity.Create("A"), lab.ClockOf("A"));
        var b = await SyncEngine.JoinAsync(lab.Cloud, lab.Secret, DeviceIdentity.Create("B"), lab.ClockOf("B"));
        a.Put(Collections.Bookmarks, "https://1.pt/", Bookmark("1"));
        await a.PushAsync();
        a.Put(Collections.Bookmarks, "https://2.pt/", Bookmark("2"));
        flaky.FailWrites = 1;
        await Assert.ThrowsAsync<IOException>(() => a.PushAsync()); // o lote 2 fica pendente
        var oldCopy = a.State.ToBytes();                          // cópia de segurança tirada AGORA

        // A continua: o lote 2 sai, e mais o 3. B lê-os.
        a.Put(Collections.Bookmarks, "https://3.pt/", Bookmark("3"));
        await a.PushAsync();
        await b.PullAsync();
        var batch2 = lab.Cloud.Files[SyncPaths.Batch(a.ChainId, a.Device.Id, 2)].ToArray();

        // O disco de A avaria e o estado volta da cópia antiga (NextSeq 3, lote 2 pendente).
        var restored = await SyncEngine.ResumeAsync(lab.Cloud, lab.Secret, SyncState.Parse(oldCopy), lab.ClockOf("A"));
        Assert.Equal("3", restored.Get(Collections.Bookmarks, "https://3.pt/")!.Text("title")); // o lote 3 é dele: lido de volta
        Assert.Equal(4, restored.State.NextSeq);
        restored.Put(Collections.Bookmarks, "https://4.pt/", Bookmark("4"));
        Assert.Equal(4, await restored.PushAsync());
        Assert.Equal(batch2, lab.Cloud.Files[SyncPaths.Batch(a.ChainId, a.Device.Id, 2)]); // nada reescrito

        await b.PullAsync();
        foreach (var i in new[] { 1, 2, 3, 4 })
            Assert.Equal($"{i}", b.Get(Collections.Bookmarks, $"https://{i}.pt/")!.Text("title"));
        Assert.Equal(restored.Fingerprint(), b.Fingerprint());
    }

    [Fact]
    public async Task RecoveryCode_RestoresEverythingOnANewDevice()
    {
        using var lab = new SyncLab();
        var a = (await lab.DevicesAsync("PC antigo"))[0];
        a.Put(Collections.Bookmarks, "https://x.pt/", Bookmark("x"));
        a.SetNotes("as minhas notas");
        await a.SyncAsync();
        var code = lab.Secret.ToCode();
        // O PC antigo avariou: o novo só tem o código, escrito à mão (minúsculas, sem hífenes).
        using var typed = AccountSecret.FromCode(code.ToLowerInvariant().Replace("-", " "));
        var b = await SyncEngine.JoinAsync(lab.Cloud, typed, DeviceIdentity.Create("PC novo"), lab.ClockOf("novo"));
        await b.PullAsync();
        Assert.Equal("x", b.Get(Collections.Bookmarks, "https://x.pt/")!.Text("title"));
        Assert.Equal("as minhas notas", b.Notes);

        using var wrong = AccountSecret.Create();
        await Assert.ThrowsAsync<ChainGoneException>(() => SyncEngine.JoinAsync(lab.Cloud, wrong, DeviceIdentity.Create("x"), lab.ClockOf("x")));
    }

    [Fact]
    public async Task NewChain_AfterALostDevice_LocksTheOldCodeOut()
    {
        using var lab = new SyncLab();
        var (a, b, lost) = await Three(lab);
        a.Put(Collections.Bookmarks, "https://x.pt/", Bookmark("x"));
        a.SetNotes("notas");
        await SyncLab.SettleAsync(a, b, lost);
        var oldChain = a.ChainId;

        var (rotated, secret) = await a.RotateChainAsync();
        Assert.NotEqual(oldChain, rotated.ChainId);
        Assert.DoesNotContain(lab.Cloud.Files.Keys, k => k.Contains(oldChain));
        await Assert.ThrowsAsync<ChainGoneException>(() => lost.PullAsync());

        // O aparelho que fica recebe o código novo e continua com tudo.
        var b2 = await SyncEngine.JoinAsync(lab.Cloud, AccountSecret.FromCode(secret.ToCode()), DeviceIdentity.Create("B"), lab.ClockOf("B"));
        await b2.PullAsync();
        Assert.Equal("x", b2.Get(Collections.Bookmarks, "https://x.pt/")!.Text("title"));
        Assert.Equal("notas", b2.Notes);
        await Assert.ThrowsAsync<ChainGoneException>(() => SyncEngine.JoinAsync(lab.Cloud, lab.Secret, DeviceIdentity.Create("ladrão"), lab.ClockOf("l")));
    }

    [Fact]
    public async Task StateSurvivesARestart()
    {
        using var lab = new SyncLab();
        var devices = await lab.DevicesAsync("A", "B");
        devices[0].Put(Collections.Bookmarks, "https://x.pt/", Bookmark("x"));
        await SyncLab.SettleAsync(devices);
        var saved = devices[1].State.ToBytes();
        var restarted = await SyncEngine.ResumeAsync(lab.Cloud, lab.Secret, SyncState.Parse(saved), lab.ClockOf("B"));
        devices[0].Put(Collections.Bookmarks, "https://y.pt/", Bookmark("y"));
        await devices[0].PushAsync();
        var result = await restarted.PullAsync();
        Assert.Equal(1, result.Applied); // só o novo; o x já lá estava
        Assert.Equal(devices[0].Fingerprint(), restarted.Fingerprint());
    }

    [Fact]
    public async Task Devices_AreListedWithNamesOnlyInsideTheCipher()
    {
        using var lab = new SyncLab();
        var devices = await lab.DevicesAsync("PC da sala", "Portátil da Ana");
        await SyncLab.SettleAsync(devices);
        var list = await devices[0].DevicesAsync();
        Assert.Equal(["PC da sala", "Portátil da Ana"], list.Select(d => d.Name).Order().ToArray());
    }

    [Fact]
    public async Task Notes_ConcurrentEditsOnDifferentLines_AllSurviveOnThreeDevices()
    {
        using var lab = new SyncLab();
        var (a, b, c) = await Three(lab);
        a.SetNotes("Compras\n- pão\n- leite\n\nIdeias\n- viagem\n\nLer\n- livro");
        await SyncLab.SettleAsync(a, b, c);
        a.SetNotes("Compras\n- pão\n- leite\n- ovos\n\nIdeias\n- viagem\n\nLer\n- livro");
        lab.Advance(TimeSpan.FromMilliseconds(5));
        b.SetNotes("Compras\n- pão\n- leite\n\nIdeias\n- viagem ao Porto\n\nLer\n- livro");
        lab.Advance(TimeSpan.FromMilliseconds(5));
        c.SetNotes("Compras\n- pão\n- leite\n\nIdeias\n- viagem\n\nLer\n- livro\n- artigo da Ana");
        await SyncLab.SettleAsync(a, b, c);
        var expected = "Compras\n- pão\n- leite\n- ovos\n\nIdeias\n- viagem ao Porto\n\nLer\n- livro\n- artigo da Ana";
        Assert.Equal(expected, a.Notes);
        Assert.Equal(expected, b.Notes);
        Assert.Equal(expected, c.Notes);
    }

    [Fact]
    public async Task Notes_SameLineEditedTwice_KeepsBothVersions_SameEverywhere()
    {
        using var lab = new SyncLab();
        var devices = await lab.DevicesAsync("Casa", "Trabalho");
        var (a, b) = (devices[0], devices[1]);
        a.SetNotes("Reunião às 10h\nLevar o portátil");
        await SyncLab.SettleAsync(a, b);
        a.SetNotes("Reunião às 11h\nLevar o portátil");
        lab.Advance(TimeSpan.FromSeconds(1));
        b.SetNotes("Reunião às 9h30\nLevar o portátil");
        await SyncLab.SettleAsync(b, a);
        Assert.Equal(a.Notes, b.Notes);
        Assert.Contains("Reunião às 11h", a.Notes);
        Assert.Contains("Reunião às 9h30", a.Notes);
        Assert.Contains("Conflito de sincronização", a.Notes);
        Assert.Contains("——— Casa ———", a.Notes);
        Assert.Equal(1, a.Notes.Split('\n').Count(l => l == "Levar o portátil"));
        // Resolvido num aparelho, a resolução chega ao outro sem novo conflito.
        a.SetNotes("Reunião às 11h\nLevar o portátil");
        await SyncLab.SettleAsync(a, b);
        Assert.Equal("Reunião às 11h\nLevar o portátil", b.Notes);
    }

    [Fact]
    public async Task Tabs_ArePerDevice_NeverMerged()
    {
        using var lab = new SyncLab();
        var devices = await lab.DevicesAsync("PC", "Portátil");
        devices[0].PublishTabs(new JsonObject { ["tabs"] = new JsonArray("https://a.pt/", "https://b.pt/") });
        devices[1].PublishTabs(new JsonObject { ["tabs"] = new JsonArray("https://c.pt/") });
        await SyncLab.SettleAsync(devices);
        var tabs = devices[0].Records(Collections.Tabs);
        Assert.Equal(2, tabs.Count);
        Assert.Contains(tabs, t => t.Key == devices[1].Device.Id && t.Data!["tabs"]!.AsArray().Count == 1);
    }

    private static async Task<(SyncEngine, SyncEngine, SyncEngine)> Three(SyncLab lab)
    {
        var d = await lab.DevicesAsync("PC-casa", "PC-trabalho", "Portátil");
        return (d[0], d[1], d[2]);
    }

    // ID opaco calculado fora do motor (para o teste dos campos desconhecidos).
    private sealed class KeyRing_Id(SyncLab lab)
    {
        public string Of(string collection, string key)
        {
            var envelope = KeyEnvelope.Parse(lab.Cloud.Files[SyncPaths.Keys(lab.Secret.ChainId)]);
            return KeyRing.Open(lab.Secret, envelope).OpaqueId(collection, key);
        }
    }

    private sealed class FlakyTransport(ISyncTransport inner) : ISyncTransport
    {
        public int FailWrites;
        public string Name => inner.Name;
        public Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken c = default) => inner.ListAsync(prefix, c);
        public Task<byte[]?> ReadAsync(string path, CancellationToken c = default) => inner.ReadAsync(path, c);
        public Task DeleteAsync(string path, CancellationToken c = default) => inner.DeleteAsync(path, c);

        public Task WriteAsync(string path, byte[] data, CancellationToken c = default)
        {
            if (path.Contains("/d/") && FailWrites-- > 0) throw new IOException("sem rede");
            return inner.WriteAsync(path, data, c);
        }
    }
}
