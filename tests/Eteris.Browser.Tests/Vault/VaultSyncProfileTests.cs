using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Eteris.Browser.Sync;
using Eteris.Browser.Tests.Sync;
using Eteris.Browser.Vault;
using Eteris.Sync.Engine;
using Eteris.Sync.Model;
using Eteris.Sync.Secrets;
using Eteris.Sync.Transport;
using Microsoft.Data.Sqlite;

namespace Eteris.Browser.Tests.Vault;

// D3 (docs/palavras-passe.md): o cofre do navegador ligado à sincronização (VaultSyncProfile, ProfileWithVault). Só
// dados inventados (cartões de teste com Luhn válido, NIF/CPF de teste).
public sealed class VaultSyncProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "eteris-cofre-sync-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
    private readonly List<VaultService> _open = [];

    private const string Site = "https://conta.banco-exemplo.test";
    private const string User = "utilizador.teste@exemplo.test";

    public VaultSyncProfileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var vault in _open) vault.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private VaultService Vault(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var vault = new VaultService(dir, new Dpapi(VaultKey.Purpose), () => _now);
        _open.Add(vault);
        return vault;
    }

    private static VaultSyncProfile Profile(VaultService vault) => new(() => vault, () => true);

    private static LoginItem Login(string password, string user = User, string site = Site)
        => new("", site + "/entrar", user, password, "nota inventada", default, default, null, null, false, null);

    private static void FillEverything(VaultService v)
    {
        v.SaveLogin(Login("Inventada-1!"));
        v.SaveGenerated("https://novo.exemplo.test/registo", "Gerada-Inventada-2?", "novo.utilizador");
        v.AddNeverSave("https://nunca.exemplo.test/");
        v.Save(new CardItem("", "Titular Inventado", "4111 1111 1111 1111", 8, 2029, "Cartão de teste", default, null));
        v.Save(new IbanItem("", "PT50000201231234567890154", "Conta de teste", default));
        v.Save(new ContactItem("", "Casa", "Maria", "Inventada", null, "Rua Inventada", "12", "2.º Esq.", null, "Lisboa", "Lisboa", "1000001", "PT",
            "maria@exemplo.test", "+351 210 000 000", default, null));
        v.Save(new IdDocItem("", IdDocType.TaxId, "529.982.247-25", "Maria Inventada", "BR", null, null, null, "nota do documento"));
        v.Save(new IdDocItem("", IdDocType.DriverLicense, "L-123456 7", "Maria Inventada", "PT", "IMT", new DateOnly(2020, 1, 2), new DateOnly(2030, 1, 2), null));
        v.Save(TravelItem.Vehicle("", "Marca", "Modelo", 2020, "Maria Inventada", "AA-00-AA", "PT", "VIN0INVENTADO0001"));
        v.Save(TravelItem.Loyalty("", "Companhia Aérea", "Milhas", "123456789", "Maria Inventada"));
    }

    private static Dictionary<string, string> Prints(VaultSyncProfile p)
        => p.Read(null).Items.ToDictionary(i => i.Collection + "/" + i.Key, i => i.Data["h"]!.GetValue<string>());

    [Fact]
    public void Read_GivesOnlyOpaqueKeysAndPrints_StableAcrossReopening()
    {
        var a = Vault("a");
        FillEverything(a);
        var profile = Profile(a);
        var (items, unreadable) = profile.Read(null);
        Assert.Empty(unreadable);
        Assert.Equal(10, items.Count);
        Assert.Equal(Collections.Secret.Order(StringComparer.Ordinal), items.Select(i => i.Collection).Distinct().Order(StringComparer.Ordinal));
        foreach (var item in items)
        {
            Assert.Matches("^[0-9a-f]{64}$", item.Key);
            Assert.Equal(["h"], item.Data.Select(p => p.Key));
            Assert.Matches("^[0-9a-f]{64}$", item.Data["h"]!.GetValue<string>());
        }
        var first = Prints(profile);
        Assert.Equal(first, Prints(profile));
        a.Dispose();
        SqliteConnection.ClearAllPools();
        var reopened = new VaultService(Path.Combine(_root, "a"), new Dpapi(VaultKey.Purpose), () => _now);
        _open.Add(reopened);
        Assert.Equal(first, Prints(Profile(reopened)));

        // Só as coleções pedidas.
        Assert.All(profile.Read(new HashSet<string> { "logins" }).Items, i => Assert.Equal("logins", i.Collection));
    }

    [Fact]
    public void EveryCollection_RoundTrips_ToAnotherComputer_WithoutEcho_AndReencryptedWithItsKey()
    {
        var a = Vault("a");
        var b = Vault("b");
        FillEverything(a);
        var pa = Profile(a);
        var pb = Profile(b);
        var changes = new List<LocalChange>();
        foreach (var item in pa.Read(null).Items)
        {
            var content = pa.ReadSecret(item.Collection, item.Key)!;
            Assert.Equal(item.Data["h"]!.GetValue<string>(), pa.Fingerprint(item.Collection, content)); // a impressão é a do conteúdo
            var key = pb.LocalKey(item.Collection, content.NaturalKey);
            Assert.NotEqual(item.Key, key); // a chave local é de cada computador
            changes.Add(new LocalChange(item.Collection, key, ProfileSync.Print(pb.Fingerprint(item.Collection, content))) { Secret = content });
        }
        Assert.Equal(changes.Count, pb.Apply(changes).Count);

        // Sem eco: o que B lê agora é exatamente o que a sincronização lhe deu.
        var after = pb.Read(null).Items.ToDictionary(i => i.Collection + "/" + i.Key, i => i.Data["h"]!.GetValue<string>());
        foreach (var change in changes) Assert.Equal(change.Data!["h"]!.GetValue<string>(), after[change.Collection + "/" + change.Key]);
        foreach (var change in changes)
            Assert.Equal(SecretSchemas.Canonical(change.Collection, change.Secret!.Data), SecretSchemas.Canonical(change.Collection, pb.ReadSecret(change.Collection, change.Key)!.Data));

        // E o cofre de B serve para o resto do Éteris (sugestões, preencher, páginas), com os derivados refeitos.
        Assert.Equal("Inventada-1!", b.GetLogin(b.LoginsFor(Site + "/").Single().Id)!.Password);
        var card = b.GetCard(b.List(VaultCollection.Cards).Single().Id)!;
        Assert.Equal("4111111111111111", card.Number);
        Assert.Equal("1111", b.List(VaultCollection.Cards).Single().Meta["last4"]!.GetValue<string>());
        Assert.True(b.IsNeverSave("https://nunca.exemplo.test/x"));
        Assert.Contains(b.Logins(), l => b.GetLogin(l.Id)!.Generated);
        Assert.Equal(2, b.List(VaultCollection.IdDocs).Count);

        // Cifrado com a chave de B: nenhum dos textos em claro no cofre.db de B.
        SqliteConnection.ClearAllPools();
        var bytes = File.ReadAllBytes(Path.Combine(_root, "b", VaultStore.FileName));
        foreach (var plain in new[] { "Inventada-1!", "banco-exemplo", "utilizador.teste", "4111111111111111", "PT50000201231234567890154", "Rua Inventada",
                     "52998224725", "AA-00-AA", "VIN0INVENTADO", "123456789", "nunca.exemplo" })
            foreach (var encoding in new[] { Encoding.Latin1, Encoding.UTF8, Encoding.Unicode })
                Assert.True(bytes.AsSpan().IndexOf(encoding.GetBytes(plain)) < 0, plain);
    }

    [Fact]
    public async Task ARemoteChange_KeepsThePreviousPasswordInTheHistory_WithANotice_AndThePageShowsIt()
    {
        using var f = new VaultFixture();
        var id = f.Vault.SaveLogin(Login("Antiga-Inventada-1"));
        var profile = new VaultSyncProfile(() => f.Vault, () => true);
        var item = profile.Read(null).Items.Single();
        var content = profile.ReadSecret(item.Collection, item.Key)!;
        content.Data["password"] = "Nova-Inventada-2";
        var applied = profile.Apply([new LocalChange(item.Collection, item.Key, ProfileSync.Print(profile.Fingerprint(item.Collection, content))) { Secret = content }]);
        Assert.Single(applied);
        Assert.Equal("Nova-Inventada-2", f.Vault.GetLogin(id)!.Password);
        Assert.Equal("Antiga-Inventada-1", f.Vault.PreviousPassword(id));
        var notice = profile.TakeNotice();
        Assert.Contains("conta.banco-exemplo.test", notice);
        Assert.Contains("a anterior ficou no histórico", notice);
        Assert.Null(profile.TakeNotice());

        // «Mostrar» (com a confirmação do Windows) mostra também a anterior; a lista nunca a tem.
        var reveal = (await f.Get("passwords", "/reveal", "id=" + id)).Body;
        Assert.Contains("\"previous\":\"Antiga-Inventada-1\"", reveal);
        Assert.DoesNotContain("Antiga-Inventada", (await f.Get("passwords", "/list")).Body);
        Assert.DoesNotContain("Antiga-Inventada", (await f.Get("passwords", "/")).Body);
    }

    [Fact]
    public void UnacceptableRemoteItems_AreLeftOut()
    {
        var v = Vault("v");
        var p = Profile(v);
        LocalChange Change(string collection, string natural, JsonObject data)
        {
            var item = new SecretItem(natural, data);
            return new LocalChange(collection, p.LocalKey(collection, natural), ProfileSync.Print(p.Fingerprint(collection, item))) { Secret = item };
        }
        var bad = new[]
        {
            Change("logins", "HTTPS://Conta.Exemplo.test\nu", new JsonObject { ["origin"] = "HTTPS://Conta.Exemplo.test", ["username"] = "u", ["password"] = "x" }), // origem por normalizar
            Change("logins", "https://a.test\noutro", new JsonObject { ["origin"] = "https://a.test", ["username"] = "u", ["password"] = "x" }),           // chave que não bate
            Change("logins", "javascript:alert(1)\nu", new JsonObject { ["origin"] = "javascript:alert(1)", ["username"] = "u" }),
            Change("cards", "c1", new JsonObject { ["number"] = "4111111111111112", ["expMonth"] = 1, ["expYear"] = 2030 }),                       // Luhn errado
            Change("cards", "c2", new JsonObject { ["number"] = "4111111111111111", ["cvc"] = "123" }),                                           // código de segurança
            Change("cards", "c3", new JsonObject { ["number"] = "4111111111111111", ["expMonth"] = "08" }),                                        // tipo errado
            Change("cards", "../../x", new JsonObject { ["number"] = "4111111111111111" }),                                                       // ID estranho
            Change("ibans", "i1", new JsonObject { ["iban"] = "PT50000201231234567890155" }),
            Change("idDocs", "d1", new JsonObject { ["type"] = "inventado", ["number"] = "1" }),
            Change("travel", "t1", new JsonObject { ["type"] = "barco" }),
        };
        Assert.Empty(p.Apply(bad));
        Assert.Empty(p.Read(null).Items);
        // Um válido do lado passa.
        Assert.Single(p.Apply([Change("cards", "c4", new JsonObject { ["number"] = "4111111111111111", ["expMonth"] = 1, ["expYear"] = 2030 })]));
    }

    [Fact]
    public void ReadingOrUsingTheVault_ChangesNothingThatSyncs()
    {
        var v = Vault("v");
        var id = v.SaveLogin(Login("Inventada-1!"));
        var p = Profile(v);
        var before = Prints(p);
        // Ler, sugerir, decifrar (o que a janela privada faz: C7) não muda a impressão; nada sobe.
        _ = v.LoginsFor(Site + "/x");
        _ = v.GetLogin(id);
        _ = v.Logins();
        Assert.Equal(before, Prints(p));
        // A «última utilização» (só gravada numa janela normal: MainWindow.RecordVaultUse pergunta à VaultWindowPolicy) fica
        // gravada mas não conta: segue com a alteração seguinte do item (SYN-03).
        _now = _now.AddMinutes(1);
        v.MarkUsed(VaultCollection.Logins, id);
        Assert.Equal(before, Prints(p));
        Assert.NotNull(v.GetLogin(id)!.Used);
        var src = File.ReadAllText(Path.Combine(GoogleSyncTests.FindRoot(), "src", "Eteris.Browser", "MainWindow.Vault.cs"));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(src, @"\.MarkUsed\("));
    }

    [Fact]
    public void WithNothingEnabled_OrNoVault_ReadingNeverOpensNorCreatesIt()
    {
        var opened = 0;
        var p = new VaultSyncProfile(() => { opened++; throw new InvalidOperationException("não devia abrir"); }, () => true);
        Assert.Empty(p.Read(new HashSet<string> { "bookmarks", "notes" }).Items);
        Assert.Equal(0, opened);

        var dir = Path.Combine(_root, "vazio");
        Directory.CreateDirectory(dir);
        var none = new VaultSyncProfile(() => { opened++; return new VaultService(dir, new Dpapi(VaultKey.Purpose)); }, () => File.Exists(Path.Combine(dir, VaultKey.FileName)));
        var (items, unreadable) = none.Read(null);
        Assert.Empty(items);
        Assert.Empty(unreadable);
        Assert.Equal(0, opened);
        Assert.Empty(Directory.EnumerateFiles(dir));
    }

    [Fact]
    public void AVaultThatDoesNotOpen_IsUnreadable_NotEmpty()
    {
        var v = Vault("v");
        v.SaveLogin(Login("x"));
        v.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(Path.Combine(_root, "v", VaultKey.FileName)); // a chave desapareceu
        var broken = new VaultService(Path.Combine(_root, "v"), new Dpapi(VaultKey.Purpose));
        _open.Add(broken);
        var (items, unreadable) = Profile(broken).Read(null);
        Assert.Empty(items);
        Assert.Equal(Collections.Secret.Order(StringComparer.Ordinal), unreadable.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AVaultFromBeforeSync_GetsTheColumns_AndItsItemsGetKeysOnFirstRead()
    {
        var v = Vault("v");
        v.SaveLogin(Login("x"));
        SqliteConnection.ClearAllPools();
        using (var c = new SqliteConnection("Data Source=" + Path.Combine(_root, "v", VaultStore.FileName) + ";Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE items SET sync_key = NULL, sync_print = NULL";
            cmd.ExecuteNonQuery();
        }
        var item = Profile(v).Read(null).Items.Single();
        Assert.Equal(item.Key, Profile(v).Read(null).Items.Single().Key);
        Assert.Equal("x", Profile(v).ReadSecret("logins", item.Key)!.Data["password"]!.GetValue<string>());

        // Um cofre.db com o esquema de antes (sem as colunas) abre e ganha-as.
        var old = Path.Combine(_root, "antigo");
        Directory.CreateDirectory(old);
        using (var c = new SqliteConnection("Data Source=" + Path.Combine(old, VaultStore.FileName) + ";Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE items(collection TEXT NOT NULL, id TEXT NOT NULL, origin_key BLOB, site_key BLOB, meta_ct BLOB, meta_version INTEGER NOT NULL DEFAULT 0, secret_ct BLOB, secret_version INTEGER NOT NULL DEFAULT 0, prev_ct BLOB, prev_version INTEGER NOT NULL DEFAULT 0, created INTEGER NOT NULL, changed INTEGER NOT NULL, deleted INTEGER, PRIMARY KEY (collection, id)); PRAGMA user_version = 1;";
            cmd.ExecuteNonQuery();
        }
        using (var store = new VaultStore(Path.Combine(old, VaultStore.FileName), new byte[32]))
            Assert.Empty(store.SyncEntries(VaultCollection.Logins));
    }

    // Revisão: importar um .eteris com segredos num computador onde o cofre não abre não deita a importação abaixo (o
    // resto já entrou): os itens do cofre ficam de fora e o resumo di-lo.
    [Fact]
    public void ImportingSecrets_IntoAVaultThatDoesNotOpen_LeavesThemOut_AndSaysSo()
    {
        var v = Vault("imp");
        v.SaveLogin(Login("x"));
        v.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(Path.Combine(_root, "imp", VaultKey.FileName));
        var broken = new VaultService(Path.Combine(_root, "imp"), new Dpapi(VaultKey.Purpose));
        _open.Add(broken);
        var items = new List<Eteris.Sync.Format.ArchiveSecret>
        {
            new("logins", new SecretItem("https://a.exemplo.test\nu", new JsonObject { ["origin"] = "https://a.exemplo.test", ["username"] = "u", ["password"] = "Inventada-Imp-1" })),
            new("logins", new SecretItem("https://b.exemplo.test\nu", new JsonObject { ["origin"] = "https://b.exemplo.test", ["username"] = "u", ["password"] = "Inventada-Imp-2" })),
        };
        var result = ProfileTransfer.TryApplySecrets(() => broken, items);
        Assert.Equal(0, result.Applied);
        Assert.Equal(2, result.LeftOut);
        var summary = ProfileTransfer.Summary(new ProfileTransfer.Imported(1, 0, 0, 0, false, "A") { VaultLeftOut = result.LeftOut });
        Assert.Contains("1 favorito", summary);
        Assert.Contains("2 itens do cofre não entraram: o cofre deste computador não abre", summary);
    }

    // Revisão: a conta tem palavras-passe de outro PC e o cofre deste não abre (a chave desapareceu, com o cofre.db lá).
    // A chave local e a impressão de cada selo não se calculam: a coleção fica ilegível nessa ronda, em vez de a ronda
    // inteira cair (os favoritos continuam a passar), e o que já estava na base não vira «apagado».
    [Theory]
    [InlineData(true)]   // a leitura vê o cofre.db e diz que não o lê
    [InlineData(false)]  // sem cofre.key, a leitura acha que não há cofre
    public async Task AVaultThatDoesNotOpenHere_DoesNotStopTheRound(bool dbSeen)
    {
        long now = 1_762_560_000_000;
        var folder = Path.Combine(_root, "pasta");
        var a = Vault("A");
        a.SaveLogin(Login("Inventada-A"));
        var plainA = new MemoryProfile();
        SyncAgent? agentA = null;
        agentA = new SyncAgent(() => new FolderTransport(folder), new ProfileWithVault(plainA, Profile(a), () => agentA?.EnabledCollections),
            new MemoryStore(), "A", () => now, (_, ct) => Task.Delay(5, ct));
        await agentA.StartAsync();
        Assert.True(await agentA.RunAsync());

        var broken = Vault("B");
        broken.SaveLogin(Login("Local-B", user: "local"));
        broken.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(Path.Combine(_root, "B", VaultKey.FileName));
        var b = new VaultService(Path.Combine(_root, "B"), new Dpapi(VaultKey.Purpose));
        _open.Add(b);
        var plainB = new MemoryProfile();
        SyncAgent? agentB = null;
        var vaultB = new VaultSyncProfile(() => b, () => dbSeen);
        agentB = new SyncAgent(() => new FolderTransport(folder), new ProfileWithVault(plainB, vaultB, () => agentB?.EnabledCollections),
            new MemoryStore(), "B", () => now, (_, ct) => Task.Delay(5, ct));
        await agentB.StartAsync();
        await agentB.JoinWithCodeAsync(agentA.RevealKit()!.Words);

        plainB.Set(Collections.Bookmarks, "https://favorito.exemplo.test/", new JsonObject { ["title"] = "Favorito de teste", ["folder"] = "" });
        for (var i = 0; i < 3; i++)
        {
            Assert.True(await agentB.RunAsync());
            Assert.True(await agentA.RunAsync());
        }
        Assert.Contains(plainA.Collect(), item => item.Collection == Collections.Bookmarks && item.Key == "https://favorito.exemplo.test/");
        // Nada se apagou na conta por o cofre do B não abrir.
        Assert.Single(a.Logins());
    }

    // Auditoria, VAU-03: o cofre.db desapareceu (ou ficou com 0 bytes) e o cofre.key ficou. O cofre não nasce vazio:
    // não abre, as coleções ficam ilegíveis e nada se apaga na conta. Apagados os dois ficheiros, a conta volta a
    // encher o cofre.
    [Theory]
    [InlineData(false)]  // ficheiro apagado
    [InlineData(true)]   // ficheiro com 0 bytes
    public async Task AVaultWhoseDatabaseVanished_IsUnreadable_AndDeletesNothing(bool empty)
    {
        long now = 1_762_560_000_000;
        var folder = Path.Combine(_root, "pasta");
        var dirA = Path.Combine(_root, "A");
        var keyA = Path.Combine(dirA, VaultKey.FileName);
        var dbA = Path.Combine(dirA, VaultStore.FileName);
        var current = Vault("A");
        current.SaveLogin(Login("Inventada-A"));
        var vaultA = new VaultSyncProfile(() => current, () => File.Exists(keyA));
        SyncAgent? agentA = null;
        agentA = new SyncAgent(() => new FolderTransport(folder), new ProfileWithVault(new MemoryProfile(), vaultA, () => agentA?.EnabledCollections),
            new MemoryStore(), "A", () => now, (_, ct) => Task.Delay(5, ct));
        await agentA.StartAsync();

        var b = Vault("B");
        SyncAgent? agentB = null;
        agentB = new SyncAgent(() => new FolderTransport(folder), new ProfileWithVault(new MemoryProfile(), Profile(b), () => agentB?.EnabledCollections),
            new MemoryStore(), "B", () => now, (_, ct) => Task.Delay(5, ct));
        await agentB.StartAsync();
        await agentB.JoinWithCodeAsync(agentA.RevealKit()!.Words);
        async Task Rounds()
        {
            for (var i = 0; i < 3; i++)
            {
                Assert.True(await agentA.RunAsync());
                Assert.True(await agentB.RunAsync());
            }
        }
        await Rounds();
        Assert.Single(b.Logins());

        // A base de dados do cofre de A desaparece; a chave fica.
        current.Dispose();
        SqliteConnection.ClearAllPools();
        if (empty) File.WriteAllBytes(dbA, []);
        else File.Delete(dbA);
        current = new VaultService(dirA, new Dpapi(VaultKey.Purpose));
        _open.Add(current);

        var (items, unreadable) = vaultA.Read(null);
        Assert.Empty(items);
        Assert.Equal(Collections.Secret.Order(StringComparer.Ordinal), unreadable.Order(StringComparer.Ordinal));
        Assert.Equal(VaultService.DatabaseGoneText, Assert.Throws<VaultUnavailableException>(() => current.Logins()).Message);
        Assert.Equal(empty, File.Exists(dbA));          // não nasceu uma base nova
        await Rounds();
        Assert.Single(b.Logins());                      // nada foi apagado na conta

        // Recomeçar: sem os dois ficheiros do cofre, nasce um cofre novo e a conta volta a enchê-lo.
        current.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(keyA);
        File.Delete(dbA);
        current = new VaultService(dirA, new Dpapi(VaultKey.Purpose));
        _open.Add(current);
        await Rounds();
        Assert.Single(current.Logins());
        Assert.Single(b.Logins());
    }

    // Dois aparelhos pela pasta local, cada um com o seu cofre real e o seu estado guardado (para os testes de SYN-03).
    private (SyncAgent Agent, VaultService Vault, MemoryStore Store, VaultSyncProfile Profile) Pair(string name, string folder, Func<long> now, MemoryStore? store = null, VaultService? vault = null)
    {
        vault ??= Vault(name);
        store ??= new MemoryStore();
        var profile = Profile(vault);
        SyncAgent? agent = null;
        agent = new SyncAgent(() => new FolderTransport(folder), new ProfileWithVault(new MemoryProfile(), profile, () => agent?.EnabledCollections),
            store, name, now, (_, ct) => Task.Delay(5, ct));
        return (agent, vault, store, profile);
    }

    // Auditoria, SYN-03: preencher num computador que ainda não recebeu a palavra-passe nova não a repõe a antiga em todos.
    [Fact]
    public async Task AutofillOnAStaleDevice_DoesNotRevertAPasswordChangedElsewhere()
    {
        long now = 1_762_560_000_000;
        var folder = Path.Combine(_root, "pasta");
        var a = Pair("A", folder, () => now);
        a.Vault.SaveLogin(Login("Antiga-1"));
        await a.Agent.StartAsync();
        var b = Pair("B", folder, () => now);
        await b.Agent.StartAsync();
        await b.Agent.JoinWithCodeAsync(a.Agent.RevealKit()!.Words);
        async Task Settle(params SyncAgent[] agents)
        {
            for (var i = 0; i < 3; i++)
                foreach (var agent in agents) Assert.True(await agent.RunAsync());
        }
        await Settle(a.Agent, b.Agent);
        Assert.Equal("Antiga-1", b.Vault.GetLogin(b.Vault.Logins().Single().Id)!.Password);

        // O A muda a palavra-passe e sincroniza; o B (sem ronda) preenche com ela.
        _now = _now.AddMinutes(5);
        now += 1000;
        var idA = a.Vault.Logins().Single().Id;
        a.Vault.SaveLogin(Login("Nova-2") with { Id = idA });
        Assert.True(await a.Agent.RunAsync());
        now += 1000;
        b.Vault.MarkUsed(VaultCollection.Logins, b.Vault.Logins().Single().Id);
        await Settle(b.Agent, a.Agent);
        Assert.Equal("Nova-2", a.Vault.GetLogin(a.Vault.Logins().Single().Id)!.Password);
        var onB = b.Vault.GetLogin(b.Vault.Logins().Single().Id)!;
        Assert.Equal("Nova-2", onB.Password);
        Assert.NotNull(onB.Used);
    }

    // Auditoria, SYN-03: as impressões gravadas antes da correção (calculadas com a «última utilização») não viram
    // alterações locais depois dela: regravam-se na primeira ronda, sem enviar nada.
    [Fact]
    public async Task OldPrints_AreNotSentAsLocalChanges_AfterTheUpgrade()
    {
        long now = 1_762_560_000_000;
        var folder = Path.Combine(_root, "pasta");
        var a = Pair("A", folder, () => now);
        a.Vault.SaveLogin(Login("Inventada-1"));
        await a.Agent.StartAsync();
        var b = Pair("B", folder, () => now);
        await b.Agent.StartAsync();
        await b.Agent.JoinWithCodeAsync(a.Agent.RevealKit()!.Words);
        async Task Settle(params SyncAgent[] agents)
        {
            for (var i = 0; i < 3; i++)
                foreach (var agent in agents) Assert.True(await agent.RunAsync());
        }
        await Settle(a.Agent, b.Agent);
        var before = a.Store.Load()!.Engine!.Records.Values.Single(r => r.Collection == Collections.Logins).Version;

        // Um cofre de antes da correção: a impressão gravada é outra (a de quando «used» entrava na conta).
        SqliteConnection.ClearAllPools();
        using (var c = new SqliteConnection("Data Source=" + Path.Combine(_root, "B", VaultStore.FileName) + ";Pooling=False"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE items SET sync_print = $p";
            cmd.Parameters.AddWithValue("$p", Enumerable.Repeat((byte)0x5A, 32).ToArray());
            cmd.ExecuteNonQuery();
        }
        var stale = Prints(b.Profile);
        var saved = b.Store.Load()!;
        var item = b.Profile.Read(null).Items.Single();
        saved.Baseline![ProfileSync.Id(item.Collection, item.Key)] = ProfileSync.Canonical(item.Collection, item.Data)!;
        b.Store.Save(saved);

        var again = Pair("B", folder, () => now, b.Store, b.Vault);
        await Settle(again.Agent, a.Agent);
        Assert.Equal(before, a.Store.Load()!.Engine!.Records.Values.Single(r => r.Collection == Collections.Logins).Version);
        Assert.NotEqual(stale, Prints(again.Profile));
        Assert.Single(b.Vault.Logins());
    }

    // Dois computadores pela pasta local, com o cofre real: guardar num aparece no outro, apagar some, mudar nos dois
    // deixa a anterior no histórico; e Pagamentos só passa depois de ligado nos dois.
    [Fact]
    public async Task TwoComputers_ThroughAFolder_WithTheRealVault()
    {
        long now = 1_762_560_000_000;
        var folder = Path.Combine(_root, "pasta");
        (SyncAgent Agent, VaultService Vault) Device(string name)
        {
            var vault = Vault(name);
            SyncAgent? agent = null;
            var profile = new ProfileWithVault(new MemoryProfile(), Profile(vault), () => agent?.EnabledCollections);
            agent = new SyncAgent(() => new FolderTransport(folder), profile, new MemoryStore(), name, () => now, (_, ct) => Task.Delay(5, ct));
            return (agent, vault);
        }
        async Task Settle(params (SyncAgent Agent, VaultService Vault)[] devices)
        {
            for (var i = 0; i < 3; i++)
                foreach (var d in devices) Assert.True(await d.Agent.RunAsync());
        }

        var a = Device("A");
        a.Vault.SaveLogin(Login("Inventada-A"));
        a.Vault.Save(new CardItem("", "Titular", "4111111111111111", 8, 2029, null, default, null));
        await a.Agent.StartAsync();
        var b = Device("B");
        await b.Agent.StartAsync();
        await b.Agent.JoinWithCodeAsync(a.Agent.RevealKit()!.Words);
        await Settle(a, b);

        Assert.Equal("Inventada-A", b.Vault.GetLogin(b.Vault.LoginsFor(Site + "/").Single().Id)!.Password);
        Assert.Empty(b.Vault.List(VaultCollection.Cards)); // Pagamentos desligado por omissão

        // Guardar no B aparece no A.
        b.Vault.SaveLogin(Login("Inventada-B", user: "outro"));
        await Settle(b, a);
        Assert.Contains(a.Vault.Logins(), l => l.Username == "outro");

        // Mudar nos dois ao mesmo tempo: fica a mais recente nos dois, a outra no histórico de quem perdeu.
        var idA = a.Vault.LoginsFor(Site + "/").Single(l => l.Username == User).Id;
        var idB = b.Vault.LoginsFor(Site + "/").Single(l => l.Username == User).Id;
        a.Vault.SaveLogin(Login("Mudada-no-A") with { Id = idA });
        Assert.True(await a.Agent.RunAsync());
        now += 1000;
        b.Vault.SaveLogin(Login("Mudada-no-B") with { Id = idB });
        await Settle(b, a);
        Assert.Equal("Mudada-no-B", a.Vault.GetLogin(idA)!.Password);
        Assert.Equal("Mudada-no-A", a.Vault.PreviousPassword(idA));
        Assert.Contains("histórico", a.Agent.Status.Notice);

        // Apagar no B some no A.
        b.Vault.Delete(VaultCollection.Logins, b.Vault.Logins().Single(l => l.Username == "outro").Id);
        await Settle(b, a);
        Assert.DoesNotContain(a.Vault.Logins(), l => l.Username == "outro");

        // Ligar Pagamentos no B: o cartão do A chega.
        b.Agent.SetCollections(["cards", "ibans"], on: true);
        await Settle(b, a);
        Assert.Empty(b.Vault.List(VaultCollection.Cards)); // o A ainda não envia cartões (desligado lá)
        a.Agent.SetCollections(["cards", "ibans"], on: true);
        await Settle(a, b);
        Assert.Equal("4111111111111111", b.Vault.GetCard(b.Vault.List(VaultCollection.Cards).Single().Id)!.Number);
    }
}
