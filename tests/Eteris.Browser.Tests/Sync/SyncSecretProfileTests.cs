using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Eteris.Sync.Crypto;
using Eteris.Sync.Engine;
using Eteris.Sync.Format;
using Eteris.Sync.Model;
using Eteris.Sync.Secrets;
using Eteris.Sync.Transport;

namespace Eteris.Browser.Tests.Sync;

// Um perfil com cofre, em memória (como o Vault/VaultSyncProfile do navegador): cada aparelho tem a sua chave local,
// por isso as chaves locais e as impressões são diferentes de aparelho para aparelho; o conteúdo só sai por ReadSecret.
internal sealed class SecretMemoryProfile : ILocalProfileReader, ISecretSource, ILocalNotices
{
    private readonly Lock _gate = new();
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
    private readonly Dictionary<(string Collection, string Natural), JsonObject> _secrets = [];
    private string? _notice;

    public MemoryProfile Plain { get; } = new();

    // Histórico local de 1 versão (como o cofre): a palavra-passe que perdeu ao aplicar uma alteração de fora.
    public Dictionary<string, string> History { get; } = new(StringComparer.Ordinal);

    public int SecretReads { get; private set; }

    // LocalKey/Fingerprint pedidas por coleção (para provar que uma coleção desligada nunca se abre).
    public Dictionary<string, int> Touched { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Unreadable { get; } = new(StringComparer.Ordinal);

    public static string LoginKey(string origin, string user) => origin + "\n" + user;

    public void SetSecret(string collection, string natural, JsonObject data)
    {
        lock (_gate) _secrets[(collection, natural)] = (JsonObject)data.DeepClone();
    }

    public void Login(string origin, string user, string password, string? note = null)
        => SetSecret(Collections.Logins, LoginKey(origin, user), new JsonObject { ["origin"] = origin, ["username"] = user, ["password"] = password, ["note"] = note });

    public void RemoveSecret(string collection, string natural)
    {
        lock (_gate) _secrets.Remove((collection, natural));
    }

    public JsonObject? Secret(string collection, string natural)
    {
        lock (_gate) return _secrets.TryGetValue((collection, natural), out var data) ? (JsonObject)data.DeepClone() : null;
    }

    public string? Password(string origin, string user) => Secret(Collections.Logins, LoginKey(origin, user))?["password"]?.GetValue<string>();

    public string SecretFingerprint()
    {
        lock (_gate)
            return string.Join("|", _secrets.OrderBy(s => s.Key.Collection, StringComparer.Ordinal).ThenBy(s => s.Key.Natural, StringComparer.Ordinal)
                .Select(s => s.Key.Collection + ":" + s.Key.Natural + "=" + SecretSchemas.Canonical(s.Key.Collection, s.Value)));
    }

    public string LocalKey(string collection, string naturalKey)
    {
        lock (_gate) Touched[collection] = Touched.GetValueOrDefault(collection) + 1;
        return Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes("k|" + collection + "\n" + naturalKey)));
    }

    public string Fingerprint(string collection, SecretItem item)
    {
        lock (_gate) Touched[collection] = Touched.GetValueOrDefault(collection) + 1;
        return Print(collection, item.Data);
    }

    private string Print(string collection, JsonObject data)
        => Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes("p|" + collection + "\n" + SecretSchemas.Canonical(collection, data))));

    private string Local(string collection, string natural)
        => Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes("k|" + collection + "\n" + natural)));

    public SecretItem? ReadSecret(string collection, string localKey)
    {
        lock (_gate)
        {
            SecretReads++;
            foreach (var ((c, natural), data) in _secrets)
                if (c == collection && Local(c, natural) == localKey) return new SecretItem(natural, (JsonObject)data.DeepClone());
            return null;
        }
    }

    public LocalRead Read()
    {
        var plain = Plain.Read();
        var items = plain.Items.ToList();
        lock (_gate)
            foreach (var ((collection, natural), data) in _secrets)
                if (!Unreadable.Contains(collection))
                    items.Add(new LocalItem(collection, Local(collection, natural), ProfileSync.Print(Print(collection, data))));
        return new LocalRead(items, [.. plain.Unreadable, .. Unreadable]);
    }

    public IReadOnlyList<LocalItem> Collect() => Read().Items;

    public IReadOnlyCollection<LocalChange> Apply(IReadOnlyList<LocalChange> changes)
    {
        var applied = new List<LocalChange>(Plain.Apply(changes.Where(c => !Collections.IsSecret(c.Collection)).ToList()));
        var changed = 0;
        lock (_gate)
        {
            foreach (var change in changes.Where(c => Collections.IsSecret(c.Collection)))
            {
                var current = _secrets.FirstOrDefault(s => s.Key.Collection == change.Collection && Local(s.Key.Collection, s.Key.Natural) == change.Key);
                if (change.Remove)
                {
                    if (current.Value is not null) _secrets.Remove(current.Key);
                    applied.Add(change);
                    continue;
                }
                var item = change.Secret!;
                Assert.Equal(change.Key, Local(change.Collection, item.NaturalKey));
                if (_secrets.TryGetValue((change.Collection, item.NaturalKey), out var old) && change.Collection == Collections.Logins
                    && old["password"]?.GetValue<string>() is { } previous && previous != item.Data["password"]?.GetValue<string>())
                {
                    History[item.NaturalKey] = previous;
                    changed++;
                }
                _secrets[(change.Collection, item.NaturalKey)] = (JsonObject)item.Data.DeepClone();
                applied.Add(change);
            }
            if (changed > 0) _notice = $"{changed} palavra(s)-passe mudaram noutro computador; as anteriores ficaram no histórico.";
        }
        return applied;
    }

    public string? TakeNotice()
    {
        lock (_gate)
        {
            var notice = _notice;
            _notice = null;
            return notice;
        }
    }
}

// D2: ProfileSync e SyncAgent com segredos (docs/palavras-passe.md, D2; docs/sincronizacao.md, «Coleções secretas»).
public sealed class SyncSecretProfileTests : IDisposable
{
    private static readonly Func<TimeSpan, CancellationToken, Task> Quick = (_, ct) => Task.Delay(5, ct);
    private long _now = 1_762_560_000_000;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "eteris-sync-secrets-" + Guid.NewGuid().ToString("N"));

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
        var agent = new SyncAgent(() => new FolderTransport(_folder), profile, store, name, Now, Quick) { PairingPoll = TimeSpan.FromMilliseconds(5) };
        return new Device(agent, profile, store);
    }

    private static async Task Settle(params Device[] devices)
    {
        for (var round = 0; round < 3; round++)
            foreach (var device in devices)
                Assert.True(await device.Agent.RunAsync());
    }

    private async Task<Device> JoinAsync(Device first, string name)
    {
        var device = NewDevice(name);
        await device.Agent.StartAsync();
        await device.Agent.JoinWithCodeAsync(first.Agent.Status.RecoveryCode ?? throw new InvalidOperationException("sem código"));
        return device;
    }

    private const string Site = "https://contas.exemplo.pt";

    [Fact]
    public void Defaults_FollowDecisionD1_AndTheChoiceIsKeptPerDevice()
    {
        var casa = NewDevice("Casa");
        var on = casa.Agent.EnabledCollections;
        foreach (var c in new[] { "bookmarks", "spaces", "settings", "shield", "notes", "logins", "loginsNever", "contacts", "travel" }) Assert.Contains(c, on);
        foreach (var c in new[] { "cards", "ibans", "idDocs", "tabs" }) Assert.DoesNotContain(c, on);
        Assert.True(SyncGroups.Find("payments")!.AskToEnable);
        Assert.True(SyncGroups.Find("documents")!.AskToEnable);
        Assert.Equal(2, SyncGroups.All.Count(g => g.AskToEnable));

        casa.Agent.SetCollections(["cards", "ibans"], on: true);
        casa.Agent.SetCollections(["notes"], on: false);
        var reopened = new SyncAgent(() => new FolderTransport(_folder), casa.Profile, casa.Store, "Casa", Now, Quick);
        Assert.Contains("cards", reopened.EnabledCollections);
        Assert.DoesNotContain("notes", reopened.EnabledCollections);
    }

    [Fact]
    public async Task NoSecretNorSite_InTheTransport_TheEngineState_TheBaseline_OrTheSnapshots()
    {
        var casa = NewDevice("Casa");
        casa.Agent.SetCollections(["cards", "ibans", "idDocs"], on: true);
        casa.Profile.Login(Site, "maria.teste", "Segredo-Inventado-42", "nota inventada");
        casa.Profile.SetSecret(Collections.Cards, "c0ffee", new JsonObject { ["name"] = "Maria Teste", ["number"] = "4111111111111111", ["expMonth"] = 8, ["expYear"] = 2029 });
        casa.Profile.SetSecret(Collections.IdDocs, "d0c", new JsonObject { ["type"] = "taxId", ["number"] = "529.982.247-25", ["country"] = "BR" });
        casa.Profile.SetSecret(Collections.Contacts, "a1", new JsonObject { ["givenName"] = "Maria", ["street"] = "Rua Inventada", ["postalCode"] = "1000-001" });
        casa.Profile.SetSecret(Collections.LoginsNever, "https://nunca.exemplo.pt", new JsonObject { ["origin"] = "https://nunca.exemplo.pt" });
        await casa.Agent.StartAsync();
        Assert.Null(casa.Agent.Status.Error);

        var trabalho = await JoinAsync(casa, "Trabalho");
        trabalho.Agent.SetCollections(["cards", "ibans", "idDocs"], on: true);
        await Settle(casa, trabalho);
        Assert.Equal("Segredo-Inventado-42", trabalho.Profile.Password(Site, "maria.teste"));
        Assert.Equal(casa.Profile.SecretFingerprint(), trabalho.Profile.SecretFingerprint());

        // Compactar escreve um instantâneo com o estado inteiro.
        _now += (long)TimeSpan.FromDays(2).TotalMilliseconds;
        await Settle(casa, trabalho);

        var plains = new[] { "contas.exemplo", "maria.teste", "Segredo-Inventado", "inventada", "4111111111111111", "529.982.247", "Rua Inventada", "nunca.exemplo", "Maria Teste" };
        var places = new List<(string Where, string Text)>();
        foreach (var file in Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories))
            places.Add((file, Encoding.UTF8.GetString(File.ReadAllBytes(file))));
        foreach (var device in new[] { casa, trabalho })
        {
            // O que o navegador guarda com DPAPI: estado do motor (registos, caixa de saída, lotes pendentes) e a base.
            var saved = device.Store.Load()!;
            places.Add(("estado " + device.Agent.DeviceName, Encoding.UTF8.GetString(Json.Bytes(saved))));
            Assert.NotNull(saved.Baseline);
            Assert.Contains(saved.Baseline!, b => b.Key.StartsWith("logins\n", StringComparison.Ordinal) && b.Value.StartsWith("{\"h\":", StringComparison.Ordinal));
        }
        foreach (var plain in plains)
            foreach (var (where, text) in places)
            {
                Assert.DoesNotContain(plain, text, StringComparison.Ordinal);
                Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(plain)), text, StringComparison.Ordinal);
            }
    }

    [Fact]
    public async Task OnlyChangedItems_AreReadFromTheVault()
    {
        var casa = NewDevice("Casa");
        for (var i = 0; i < 40; i++) casa.Profile.Login($"https://site{i}.exemplo.pt", "utilizador", $"senha-{i}");
        await casa.Agent.StartAsync();
        Assert.Equal(40, casa.Profile.SecretReads); // a primeira junção sobe tudo

        // Contar as alterações (o agendador, depois de cada alteração local) não decifra nada.
        var reads = casa.Profile.SecretReads;
        Assert.False(casa.Agent.HasLocalChanges());
        casa.Profile.Login("https://site7.exemplo.pt", "utilizador", "mudou");
        Assert.True(casa.Agent.HasLocalChanges());
        Assert.Equal(reads, casa.Profile.SecretReads);

        Assert.True(await casa.Agent.RunAsync());
        Assert.Equal(reads + 1, casa.Profile.SecretReads);
        Assert.True(await casa.Agent.RunAsync());
        Assert.Equal(reads + 1, casa.Profile.SecretReads);
    }

    [Fact]
    public async Task Conflict_TheNewerWins_AndTheLoserGoesToTheLocalHistory()
    {
        var casa = NewDevice("Casa");
        casa.Profile.Login(Site, "maria.teste", "original");
        await casa.Agent.StartAsync();
        var trabalho = await JoinAsync(casa, "Trabalho");
        await Settle(casa, trabalho);
        Assert.Equal("original", trabalho.Profile.Password(Site, "maria.teste"));

        casa.Profile.Login(Site, "maria.teste", "da-casa");
        Assert.True(await casa.Agent.RunAsync());
        _now += 1000;
        trabalho.Profile.Login(Site, "maria.teste", "do-trabalho"); // ainda sem a da casa
        await Settle(trabalho, casa);

        Assert.Equal("do-trabalho", casa.Profile.Password(Site, "maria.teste"));
        Assert.Equal("do-trabalho", trabalho.Profile.Password(Site, "maria.teste"));
        // A da casa não se perdeu sem rasto: ficou no histórico do cofre da casa, com um aviso.
        Assert.Equal("da-casa", casa.Profile.History[SecretMemoryProfile.LoginKey(Site, "maria.teste")]);
        Assert.Contains("ficaram no histórico", casa.Agent.Status.Notice);
    }

    [Fact]
    public async Task FirstJoin_WhatIsOnlyHereGoesUp_AndTheAccountWins_KeepingThisOneInTheHistory()
    {
        var casa = NewDevice("Casa");
        casa.Profile.Login(Site, "maria.teste", "da-conta");
        casa.Profile.Login("https://so-na-casa.exemplo.pt", "x", "1");
        await casa.Agent.StartAsync();

        var trabalho = NewDevice("Trabalho");
        trabalho.Profile.Login(Site, "maria.teste", "deste-pc");
        trabalho.Profile.Login("https://so-no-trabalho.exemplo.pt", "y", "2");
        await trabalho.Agent.StartAsync();
        await trabalho.Agent.JoinWithCodeAsync(casa.Agent.Status.RecoveryCode!);
        await Settle(casa, trabalho);

        foreach (var device in new[] { casa, trabalho })
        {
            Assert.Equal("da-conta", device.Profile.Password(Site, "maria.teste"));
            Assert.Equal("1", device.Profile.Password("https://so-na-casa.exemplo.pt", "x"));
            Assert.Equal("2", device.Profile.Password("https://so-no-trabalho.exemplo.pt", "y"));
        }
        Assert.Equal("deste-pc", trabalho.Profile.History[SecretMemoryProfile.LoginKey(Site, "maria.teste")]);
        Assert.Equal(casa.Profile.SecretFingerprint(), trabalho.Profile.SecretFingerprint());
    }

    [Fact]
    public async Task ADisabledCollection_NeverOpensSeals_NeverSends_AndEnablingItJoinsOnlyThatOne()
    {
        var casa = NewDevice("Casa");
        casa.Agent.SetCollections(["cards", "ibans"], on: true);
        casa.Profile.SetSecret(Collections.Cards, "c-casa", new JsonObject { ["number"] = "4111111111111111", ["expMonth"] = 1, ["expYear"] = 2030 });
        casa.Profile.SetSecret(Collections.Cards, "c-comum", new JsonObject { ["number"] = "5555555555554444", ["nickname"] = "da conta", ["expMonth"] = 2, ["expYear"] = 2030 });
        await casa.Agent.StartAsync();

        // Trabalho: «Pagamentos» desligado (por omissão).
        var trabalho = NewDevice("Trabalho");
        trabalho.Profile.SetSecret(Collections.Cards, "c-trabalho", new JsonObject { ["number"] = "4012888888881881", ["expMonth"] = 3, ["expYear"] = 2031 });
        trabalho.Profile.SetSecret(Collections.Cards, "c-comum", new JsonObject { ["number"] = "5555555555554444", ["nickname"] = "do trabalho", ["expMonth"] = 2, ["expYear"] = 2030 });
        trabalho.Profile.Login(Site, "maria.teste", "1");
        await trabalho.Agent.StartAsync();
        await trabalho.Agent.JoinWithCodeAsync(casa.Agent.Status.RecoveryCode!);
        await Settle(casa, trabalho);

        Assert.Equal(0, trabalho.Profile.Touched.GetValueOrDefault(Collections.Cards)); // nenhum selo de cartão aberto
        Assert.Null(trabalho.Profile.Secret(Collections.Cards, "c-casa"));                 // nada aplicado
        Assert.Null(casa.Profile.Secret(Collections.Cards, "c-trabalho"));                 // nada enviado
        Assert.Equal("1", casa.Profile.Password(Site, "maria.teste"));                     // o resto passa

        // Ligar: primeira junção só dos cartões (o que só existe aqui sobe; com a mesma chave, vale o da conta).
        trabalho.Agent.SetCollections(["cards", "ibans"], on: true);
        Assert.True(trabalho.Agent.HasLocalChanges());
        await Settle(trabalho, casa);
        foreach (var device in new[] { casa, trabalho })
        {
            Assert.NotNull(device.Profile.Secret(Collections.Cards, "c-casa"));
            Assert.NotNull(device.Profile.Secret(Collections.Cards, "c-trabalho"));
            Assert.Equal("da conta", device.Profile.Secret(Collections.Cards, "c-comum")!["nickname"]!.GetValue<string>());
        }

        // Desligar outra vez não apaga nada (nem aqui nem na conta), e o que muda na conta deixa de chegar.
        trabalho.Agent.SetCollections(["cards", "ibans"], on: false);
        casa.Profile.RemoveSecret(Collections.Cards, "c-casa");
        await Settle(casa, trabalho);
        Assert.NotNull(trabalho.Profile.Secret(Collections.Cards, "c-casa"));
        Assert.NotNull(casa.Profile.Secret(Collections.Cards, "c-trabalho"));
    }

    [Fact]
    public async Task ThreeDevices_Converge_AndTombstonesHold()
    {
        var a = NewDevice("A");
        a.Profile.Login(Site, "u1", "a1");
        await a.Agent.StartAsync();
        var b = await JoinAsync(a, "B");
        var c = await JoinAsync(a, "C");
        await Settle(a, b, c);

        b.Profile.Login(Site, "u2", "b2");
        c.Profile.SetSecret(Collections.Contacts, "casa", new JsonObject { ["label"] = "Casa", ["city"] = "Lisboa" });
        a.Profile.SetSecret(Collections.Travel, "v1", new JsonObject { ["type"] = "vehicle", ["make"] = "Marca", ["year"] = 2020, ["plate"] = "AA-00-AA" });
        await Settle(a, b, c);
        _now += 500;
        c.Profile.Login(Site, "u1", "c1");
        a.Profile.SetSecret(Collections.Contacts, "casa", new JsonObject { ["label"] = "Casa", ["city"] = "Porto" });
        await Settle(c, a, b);

        Assert.Equal(a.Profile.SecretFingerprint(), b.Profile.SecretFingerprint());
        Assert.Equal(a.Profile.SecretFingerprint(), c.Profile.SecretFingerprint());
        Assert.Equal("c1", b.Profile.Password(Site, "u1"));

        // Apagar em B some em A e em C, e não volta (nem depois de C mudar outra coisa).
        b.Profile.RemoveSecret(Collections.Logins, SecretMemoryProfile.LoginKey(Site, "u2"));
        await Settle(b, a, c);
        c.Profile.Login(Site, "u3", "c3");
        await Settle(c, a, b);
        foreach (var device in new[] { a, b, c })
        {
            Assert.Null(device.Profile.Password(Site, "u2"));
            Assert.Equal("c3", device.Profile.Password(Site, "u3"));
        }
        Assert.Equal(a.Profile.SecretFingerprint(), c.Profile.SecretFingerprint());
    }

    [Fact]
    public async Task ASealThatDoesNotOpen_IsLeftOut_AndDeletesNothingHere()
    {
        using var secret = AccountSecret.Create();
        var engine = await SyncEngine.CreateAsync(new FolderTransport(_folder), secret, DeviceIdentity.Create("A"), Now);
        var profile = new SecretMemoryProfile();
        profile.Login(Site, "u1", "1");
        profile.Login(Site, "u2", "2");
        var (_, baseline) = await ProfileSync.RunAsync(engine, profile, null, "A");
        await engine.PushAsync();

        // Um selo estragado (por exemplo, de um Éteris mais recente que este não abre).
        var record = engine.Get(Collections.Logins, engine.SecretKey(Collections.Logins, SecretMemoryProfile.LoginKey(Site, "u1")))!;
        engine.State.Records[SyncState.RecordKey(record.Collection, record.Id)] = record with { Data = new JsonObject { ["v"] = 9, ["n"] = "x", ["c"] = "y" } };
        var (report, _) = await ProfileSync.RunAsync(engine, profile, baseline, "A");
        Assert.True(report.SecretErrors > 0);
        Assert.Equal("1", profile.Password(Site, "u1"));
        Assert.Equal("2", profile.Password(Site, "u2"));

        // Nem uma primeira junção (outro aparelho, a mesma chave) escreve por cima do registo que não abre.
        var broken = engine.Get(Collections.Logins, engine.SecretKey(Collections.Logins, SecretMemoryProfile.LoginKey(Site, "u1")))!;
        var other = new SecretMemoryProfile();
        other.Login(Site, "u1", "outra");
        await ProfileSync.RunAsync(engine, other, null, "B");
        Assert.Equal(broken, engine.Get(Collections.Logins, engine.SecretKey(Collections.Logins, SecretMemoryProfile.LoginKey(Site, "u1"))));
    }

    // Aprovar um aparelho novo com segredos na conta pede o Windows Hello mesmo com as linhas do cofre desligadas aqui
    // (o aparelho novo recebe a conta inteira).
    [Fact]
    public async Task HasSecrets_CountsTheAccount_EvenWithTheVaultLinesOffHere()
    {
        var casa = NewDevice("Casa");
        casa.Profile.Login(Site, "u1", "1");
        await casa.Agent.StartAsync();
        var trabalho = NewDevice("Trabalho");
        trabalho.Agent.SetCollections(Collections.Secret, on: false);
        Assert.False(trabalho.Agent.HasSecrets);
        await trabalho.Agent.StartAsync();
        await trabalho.Agent.JoinWithCodeAsync(casa.Agent.Status.RecoveryCode!);
        Assert.True(await trabalho.Agent.RunAsync());
        Assert.Null(trabalho.Profile.Password(Site, "u1"));
        Assert.True(trabalho.Agent.HasSecrets);
    }

    [Fact]
    public async Task AnUnreadableVault_SendsNoRemovals()
    {
        var casa = NewDevice("Casa");
        casa.Profile.Login(Site, "u1", "1");
        await casa.Agent.StartAsync();
        var trabalho = await JoinAsync(casa, "Trabalho");
        await Settle(casa, trabalho);

        casa.Profile.Unreadable.Add(Collections.Logins); // DPAPI falhou, cofre ocupado…
        await Settle(casa, trabalho);
        Assert.Equal("1", trabalho.Profile.Password(Site, "u1"));
        casa.Profile.Unreadable.Clear();
        await Settle(casa, trabalho);
        Assert.Equal("1", trabalho.Profile.Password(Site, "u1"));
    }
}
