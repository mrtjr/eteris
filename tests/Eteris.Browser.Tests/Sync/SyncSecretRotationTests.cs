using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Eteris.Sync.Crypto;
using Eteris.Sync.Engine;
using Eteris.Sync.Model;
using Eteris.Sync.Secrets;
using Eteris.Sync.Transport;

namespace Eteris.Browser.Tests.Sync;

// D5 (docs/palavras-passe.md; docs/sincronizacao.md, «Coleções secretas»): «nova cadeia» volta a selar os segredos com a
// K_seg nova; o aparelho removido não abre nada; a documentação lista as coleções secretas.
public sealed class SyncSecretRotationTests : IDisposable
{
    private static readonly Func<TimeSpan, CancellationToken, Task> Quick = (_, ct) => Task.Delay(5, ct);
    private long _now = 1_762_560_000_000;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "eteris-sync-rotation-" + Guid.NewGuid().ToString("N"));

    private long Now() => _now;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
    }

    private sealed record Device(SyncAgent Agent, SecretMemoryProfile Profile, MemoryStore Store);

    private Device NewDevice(string name)
    {
        var profile = new SecretMemoryProfile();
        var store = new MemoryStore();
        return new Device(new SyncAgent(() => new FolderTransport(_folder), profile, store, name, Now, Quick) { PairingPoll = TimeSpan.FromMilliseconds(5) }, profile, store);
    }

    private static async Task Settle(params Device[] devices)
    {
        for (var round = 0; round < 3; round++)
            foreach (var device in devices)
                Assert.True(await device.Agent.RunAsync());
    }

    private const string Site = "https://contas.exemplo.pt";

    private static List<SyncRecord> Secrets(MemoryStore store)
        => store.Load()!.Engine!.Records.Values.Where(r => Collections.IsSecret(r.Collection) && !r.Deleted).ToList();

    [Fact]
    public async Task RemovingADevice_ResealsEverySecret_AndTheRemovedOneOpensNothing()
    {
        var casa = NewDevice("Casa");
        casa.Profile.Login(Site, "maria.teste", "Inventada-1");
        casa.Profile.SetSecret(Collections.Contacts, "a1", new JsonObject { ["city"] = "Lisboa" });
        await casa.Agent.StartAsync();
        var code = casa.Agent.RevealKit()!.Words;
        var trabalho = NewDevice("Trabalho");
        await trabalho.Agent.StartAsync();
        await trabalho.Agent.JoinWithCodeAsync(code);
        var portatil = NewDevice("Portátil");
        await portatil.Agent.StartAsync();
        await portatil.Agent.JoinWithCodeAsync(code);
        portatil.Profile.Login(Site, "do.portatil", "Inventada-P");
        await Settle(casa, trabalho, portatil);
        Assert.Equal("Inventada-1", portatil.Profile.Password(Site, "maria.teste"));

        var oldSeals = casa.Store.Load()!.Engine!.Seals!;
        var oldSecrets = Secrets(casa.Store);
        Assert.Equal(3, oldSecrets.Count);
        var portatilState = portatil.Store.Load()!.Engine!;

        var devices = await casa.Agent.RefreshDevicesAsync();
        await casa.Agent.RemoveDeviceAsync(devices.Single(d => d.Name == "Portátil").Id);
        Assert.Contains("O aparelho removido já tinha as suas palavras-passe: mude as dos sites importantes.", casa.Agent.Status.Notice);
        Assert.True(await trabalho.Agent.RunAsync());
        await Settle(casa, trabalho);

        // Depois da nova cadeia, os selos (e as chaves opacas) são outros: só abrem com a K_seg nova.
        var newSeals = casa.Store.Load()!.Engine!.Seals!;
        Assert.NotEqual(oldSeals.Chain, newSeals.Chain);
        Assert.NotEqual(oldSeals.SealKey, newSeals.SealKey);
        var resealed = Secrets(casa.Store);
        Assert.Equal(3, resealed.Count);
        Assert.Empty(resealed.Select(r => r.Key).Intersect(oldSecrets.Select(r => r.Key)));
        foreach (var record in resealed)
        {
            Assert.NotNull(newSeals.Open(record.Collection, record.Id, record.Data));
            Assert.Throws<SecretSealException>(() => oldSeals.Open(record.Collection, record.Id, record.Data));
            Assert.Throws<SecretSealException>(() => (oldSeals with { Chain = newSeals.Chain }).Open(record.Collection, record.Id, record.Data));
        }

        // Os que ficam continuam iguais e a sincronizar.
        Assert.Equal(casa.Profile.SecretFingerprint(), trabalho.Profile.SecretFingerprint());
        Assert.Equal("Inventada-P", trabalho.Profile.Password(Site, "do.portatil"));
        trabalho.Profile.Login(Site, "maria.teste", "Mudada-depois");
        await Settle(trabalho, casa);
        Assert.Equal("Mudada-depois", casa.Profile.Password(Site, "maria.teste"));

        // O removido para; com o que tinha (o código antigo, as chaves antigas) não abre nada da cadeia nova.
        Assert.True(await portatil.Agent.RunAsync());
        Assert.Equal(SyncPhase.Stopped, portatil.Agent.Status.Phase);
        Assert.NotEqual("Mudada-depois", portatil.Profile.Password(Site, "maria.teste"));
        using var oldAccount = AccountSecret.FromCode(code);
        await Assert.ThrowsAsync<ChainGoneException>(() => SyncEngine.JoinAsync(new FolderTransport(_folder), oldAccount, DeviceIdentity.Create("x"), Now));
        foreach (var record in resealed)
            Assert.Throws<SecretSealException>(() => portatilState.Seals!.Open(record.Collection, record.Id, record.Data));
        var newChain = Path.Combine(_folder, "eteris", newSeals.Chain);
        Assert.True(Directory.Exists(newChain));
        foreach (var file in Directory.EnumerateFiles(newChain, "*", SearchOption.AllDirectories))
            foreach (var plain in new[] { "contas.exemplo", "maria.teste", "Mudada-depois", "Inventada", "Lisboa" })
                Assert.DoesNotContain(plain, Encoding.UTF8.GetString(File.ReadAllBytes(file)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANewRecoveryCode_AlsoReseals_WithoutTheRemovedDeviceWarning()
    {
        var casa = NewDevice("Casa");
        casa.Profile.Login(Site, "u", "1");
        await casa.Agent.StartAsync();
        casa.Agent.MarkKitSaved();
        var before = Secrets(casa.Store).Single();
        await casa.Agent.NewRecoveryCodeAsync();
        Assert.DoesNotContain("removido", casa.Agent.Status.Notice);
        var after = Secrets(casa.Store).Single();
        Assert.NotEqual(before.Key, after.Key);
        Assert.Equal("1", casa.Store.Load()!.Engine!.Seals!.Open(after.Collection, after.Id, after.Data).Data["password"]!.GetValue<string>());
        Assert.True(await casa.Agent.RunAsync());
        Assert.Equal("1", casa.Profile.Password(Site, "u"));
    }

    // Um aparelho que fica e recebe a conta nova volta a selar o que ELE escreveu (RestageOwn), com as chaves dos selos
    // da cadeia antiga guardadas no seu estado (a cadeia antiga já não existe quando ele dá por isso).
    [Fact]
    public async Task RestageOwn_ResealsThisDevicesSecrets_WithTheOldSealKeysFromItsState()
    {
        using var first = AccountSecret.Create();
        var device = DeviceIdentity.Create("Trabalho");
        var old = await SyncEngine.CreateAsync(new FolderTransport(_folder), first, device, Now);
        old.PutSecret(Collections.Logins, new SecretItem(Site + "\nu", new JsonObject { ["origin"] = Site, ["username"] = "u", ["password"] = "Inventada-R" }));
        old.DeleteSecret(Collections.Cards, "c-apagado");
        var saved = SyncState.Parse(old.State.ToBytes()); // como o navegador o guarda (ida e volta JSON)
        Assert.NotNull(saved.Seals);

        using var second = AccountSecret.Create();
        var fresh = await SyncEngine.CreateAsync(new FolderTransport(_folder), second, device, Now);
        fresh.RestageOwn(saved);
        var login = fresh.Get(Collections.Logins, fresh.SecretKey(Collections.Logins, Site + "\nu"))!;
        Assert.Equal("Inventada-R", fresh.OpenSecret(login)!.Data["password"]!.GetValue<string>());
        Assert.True(fresh.State.Outbox.ContainsKey(SyncState.RecordKey(login.Collection, login.Id)));
        // A lápide também passa (com a chave natural selada), para o apagamento não se perder na troca.
        var tomb = fresh.Get(Collections.Cards, fresh.SecretKey(Collections.Cards, "c-apagado"))!;
        Assert.True(tomb.Deleted);
        Assert.Equal("c-apagado", fresh.OpenSecret(tomb)!.NaturalKey);

        // Um estado de antes destas chaves (sem «seal»): os secretos não passam (o perfil volta a enviá-los se mudarem).
        var legacy = SyncState.Parse(old.State.ToBytes());
        legacy.Seals = null;
        using var third = AccountSecret.Create();
        var other = await SyncEngine.CreateAsync(new FolderTransport(_folder), third, DeviceIdentity.Create("Outro"), Now);
        other.RestageOwn(legacy);
        Assert.DoesNotContain(other.State.Records.Values, r => Collections.IsSecret(r.Collection));
    }

    [Fact]
    public void TheSyncDoc_ListsTheSecretCollections_TheInnerCipher_AndTheThreats()
    {
        var doc = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "docs", "sincronizacao.md"), Encoding.UTF8);
        Assert.Contains("## Coleções secretas", doc);
        foreach (var collection in Collections.Secret) Assert.Contains($"`{collection}`", doc);
        foreach (var (collection, fields) in SecretSchemas.Fields)
            foreach (var field in fields.Keys)
                Assert.Contains($"`{field}`", doc);
        foreach (var text in new[] { "secrets:v1", "secret-keys:v1", "eteris-secret|v1|", "O que passa entre PCs", "Quem tem o kit de recuperação ou um computador emparelhado tem também as palavras-passe", // 0.2.15: o código passou a kit
                     "o número de palavras-passe também pesa", "mude as dos sites importantes" })
            Assert.Contains(text, doc);
    }
}
